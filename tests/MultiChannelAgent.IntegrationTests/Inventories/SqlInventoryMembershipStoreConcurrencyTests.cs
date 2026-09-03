using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.IntegrationTests.Inventories;

/// <summary>
/// Fast, Docker-free regression coverage for the atomicity <see cref="SqlInventoryMembershipStore"/>
/// must guarantee under real contention: two independent <see cref="MultiChannelAgentDbContext"/>
/// instances - each its own real SQLite connection into one shared-cache in-memory database,
/// mirroring two separate HTTP request scopes - both attempt to mutate the SAME target Participant's
/// Membership row on the SAME Inventory at the same instant. This is exactly the kind of race an
/// in-flight ownership transfer or orphan recovery (both of which also bump this row's
/// <see cref="MembershipEntity.ConcurrencyStamp"/>, per <see cref="SqlInventoryOwnershipStore"/> and
/// <see cref="SqlInventoryRecoveryStore"/>) can trigger against this store. Exactly one writer may
/// commit; the loser must report a typed <c>ConcurrentModification</c> outcome rather than leaking a
/// raw <see cref="DbUpdateConcurrencyException"/>.
/// </summary>
public sealed class SqlInventoryMembershipStoreConcurrencyTests : IDisposable
{
    private readonly SqliteConnection _keepAliveConnection;
    private readonly string _connectionString;

    public SqlInventoryMembershipStoreConcurrencyTests()
    {
        _connectionString = $"DataSource=file:{Guid.NewGuid()}?mode=memory&cache=shared";
        _keepAliveConnection = new SqliteConnection(_connectionString);
        _keepAliveConnection.Open();

        using var db = CreateContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _keepAliveConnection.Dispose();

    private async Task<(Guid InventoryId, Guid OwnerId, Guid TargetId)> SeedInventoryWithOwnerAndEditorAsync()
    {
        var inventoryId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        using var db = CreateContext();
        db.Participants.Add(new ParticipantEntity { Id = ownerId, DisplayName = "Owner", CreatedAt = now, UpdatedAt = now });
        db.Participants.Add(new ParticipantEntity { Id = targetId, DisplayName = "Target", CreatedAt = now, UpdatedAt = now });
        db.Inventories.Add(new InventoryEntity
        {
            Id = inventoryId,
            Name = "Race Warehouse",
            NormalizedName = "race warehouse",
            CreatedByParticipantId = ownerId,
            ClientRequestId = "seed-membership-race",
            CreatedAt = now,
        });
        db.Memberships.Add(new MembershipEntity { InventoryId = inventoryId, ParticipantId = ownerId, Role = MembershipRole.Owner, CreatedAt = now });
        db.Memberships.Add(new MembershipEntity { InventoryId = inventoryId, ParticipantId = targetId, Role = MembershipRole.Editor, CreatedAt = now });
        await db.SaveChangesAsync(CancellationToken.None);

        return (inventoryId, ownerId, targetId);
    }

    [Fact]
    public async Task Two_concurrent_role_changes_for_the_same_target_never_leak_a_concurrency_exception()
    {
        var (inventoryId, ownerId, targetId) = await SeedInventoryWithOwnerAndEditorAsync();
        var now = DateTimeOffset.UtcNow;

        using var barrier = new Barrier(2);
        using var dbA = CreateContext(new SynchronizeFirstReadInterceptor(barrier));
        using var dbB = CreateContext(new SynchronizeFirstReadInterceptor(barrier));

        var storeA = new SqlInventoryMembershipStore(dbA);
        var storeB = new SqlInventoryMembershipStore(dbB);

        // Both attempts race to change the SAME existing target row's role - exactly the kind of
        // concurrent write to a Membership row that an in-flight ownership transfer or recovery
        // (which also bump this row's ConcurrencyStamp) can trigger in production.
        var taskA = Task.Run(() => storeA.GrantOrChangeRoleAsync(
            new InventoryId(inventoryId), new ParticipantId(ownerId), new ParticipantId(targetId), "Target", MembershipRole.Viewer, now, CancellationToken.None));
        var taskB = Task.Run(() => storeB.GrantOrChangeRoleAsync(
            new InventoryId(inventoryId), new ParticipantId(ownerId), new ParticipantId(targetId), "Target", MembershipRole.Editor, now, CancellationToken.None));

        var results = await Task.WhenAll(taskA, taskB);

        Assert.Single(results, r => r.Outcome == MembershipGrantOutcome.RoleChanged);
        Assert.Single(results, r => r.Outcome == MembershipGrantOutcome.ConcurrentModification);

        using var verifyDb = CreateContext();
        var targetRow = await verifyDb.Memberships.AsNoTracking().SingleAsync(m => m.InventoryId == inventoryId && m.ParticipantId == targetId);
        Assert.True(targetRow.Role is MembershipRole.Viewer or MembershipRole.Editor);
    }

    [Fact]
    public async Task Two_concurrent_removals_of_the_same_target_never_leak_a_concurrency_exception()
    {
        var (inventoryId, ownerId, targetId) = await SeedInventoryWithOwnerAndEditorAsync();
        var now = DateTimeOffset.UtcNow;

        using var barrier = new Barrier(2);
        using var dbA = CreateContext(new SynchronizeFirstReadInterceptor(barrier));
        using var dbB = CreateContext(new SynchronizeFirstReadInterceptor(barrier));

        var storeA = new SqlInventoryMembershipStore(dbA);
        var storeB = new SqlInventoryMembershipStore(dbB);

        var taskA = Task.Run(() => storeA.RemoveAsync(new InventoryId(inventoryId), new ParticipantId(ownerId), new ParticipantId(targetId), now, CancellationToken.None));
        var taskB = Task.Run(() => storeB.RemoveAsync(new InventoryId(inventoryId), new ParticipantId(ownerId), new ParticipantId(targetId), now, CancellationToken.None));

        var results = await Task.WhenAll(taskA, taskB);

        Assert.Single(results, r => r.Outcome == MembershipRemovalOutcome.Removed);
        Assert.Single(results, r => r.Outcome == MembershipRemovalOutcome.ConcurrentModification);

        using var verifyDb = CreateContext();
        var remaining = await verifyDb.Memberships
            .AsNoTracking()
            .Where(m => m.InventoryId == inventoryId && m.ParticipantId == targetId)
            .ToListAsync();

        Assert.Empty(remaining);
    }

    [Fact]
    public async Task An_unrelated_DbUpdateException_still_propagates_rather_than_being_treated_as_a_concurrency_conflict()
    {
        var (inventoryId, ownerId, _) = await SeedInventoryWithOwnerAndEditorAsync();
        var now = DateTimeOffset.UtcNow;

        // A target Participant id that was never persisted violates the Membership -> Participant
        // foreign key on insert - a real, unrelated DbUpdateException, never a concurrency conflict.
        var neverPersistedTargetId = Guid.NewGuid();

        using var db = CreateContext();
        var store = new SqlInventoryMembershipStore(db);

        await Assert.ThrowsAsync<DbUpdateException>(() => store.GrantOrChangeRoleAsync(
            new InventoryId(inventoryId), new ParticipantId(ownerId), new ParticipantId(neverPersistedTargetId), "Ghost", MembershipRole.Viewer, now, CancellationToken.None));
    }

    /// <summary>Pauses the first read against <c>Memberships</c> so both racing attempts read the current row's ConcurrencyStamp before either commits.</summary>
    private sealed class SynchronizeFirstReadInterceptor(Barrier checkArrivalBarrier) : DbCommandInterceptor
    {
        private bool _synchronized;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (!_synchronized && command.CommandText.Contains("Memberships", StringComparison.Ordinal))
            {
                _synchronized = true;
                await Task.Run(() => checkArrivalBarrier.SignalAndWait(cancellationToken), cancellationToken);
            }

            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private MultiChannelAgentDbContext CreateContext(DbCommandInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<MultiChannelAgentDbContext>().UseSqlite(_connectionString);
        if (interceptor is not null)
        {
            builder.AddInterceptors(interceptor);
        }

        return new MultiChannelAgentDbContext(builder.Options);
    }
}
