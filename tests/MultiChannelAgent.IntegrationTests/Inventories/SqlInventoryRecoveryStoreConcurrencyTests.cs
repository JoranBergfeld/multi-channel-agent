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
/// Fast, Docker-free regression coverage for the "recovery race rechecks orphaned" invariant
/// <see cref="SqlInventoryRecoveryStore"/> must guarantee: two independent
/// <see cref="MultiChannelAgentDbContext"/> instances - each its own real SQLite connection into one
/// shared-cache in-memory database - both attempt to recover ownership of the SAME orphaned Inventory
/// to two different targets at the same instant. Exactly one may commit; the loser must report
/// <see cref="RecoveryOutcome.ConcurrentModification"/> rather than either silently succeeding or
/// leaking a raw <see cref="DbUpdateConcurrencyException"/> - and the Inventory must end up with
/// exactly one Owner.
/// </summary>
public sealed class SqlInventoryRecoveryStoreConcurrencyTests : IDisposable
{
    private readonly SqliteConnection _keepAliveConnection;
    private readonly string _connectionString;

    public SqlInventoryRecoveryStoreConcurrencyTests()
    {
        _connectionString = $"DataSource=file:{Guid.NewGuid()}?mode=memory&cache=shared";
        _keepAliveConnection = new SqliteConnection(_connectionString);
        _keepAliveConnection.Open();

        using var db = CreateContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _keepAliveConnection.Dispose();

    /// <summary>A directory double that resolves only <paramref name="Resolvable"/> identities - registered once up front so both racing attempts observe the same orphaned/eligible state.</summary>
    private sealed class FixedTenantMemberDirectory(IReadOnlyDictionary<Guid, ResolvedTenantMember> Resolvable) : ITenantMemberDirectory
    {
        public Task<ResolvedTenantMember?> ResolveAsync(TenantMemberIdentifier identifier, CancellationToken cancellationToken) =>
            Task.FromResult(identifier.ObjectId is { } objectId && Resolvable.TryGetValue(objectId, out var member) ? member : null);
    }

    private async Task<(Guid InventoryId, Guid OrphanedOwnerId, Guid TargetAId, Guid TargetBId)> SeedOrphanedInventoryAsync()
    {
        var inventoryId = Guid.NewGuid();
        var orphanedOwnerId = Guid.NewGuid();
        var targetAId = Guid.NewGuid();
        var targetBId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        using var db = CreateContext();
        db.Participants.Add(new ParticipantEntity { Id = orphanedOwnerId, DisplayName = "Orphaned Owner", IsActive = false, CreatedAt = now, UpdatedAt = now });
        db.Participants.Add(new ParticipantEntity { Id = targetAId, DisplayName = "Target A", CreatedAt = now, UpdatedAt = now });
        db.Participants.Add(new ParticipantEntity { Id = targetBId, DisplayName = "Target B", CreatedAt = now, UpdatedAt = now });
        db.Inventories.Add(new InventoryEntity
        {
            Id = inventoryId,
            Name = "Orphaned Warehouse",
            NormalizedName = "orphaned warehouse",
            CreatedByParticipantId = orphanedOwnerId,
            ClientRequestId = "seed-orphan-race",
            CreatedAt = now,
        });
        db.Memberships.Add(new MembershipEntity { InventoryId = inventoryId, ParticipantId = orphanedOwnerId, Role = MembershipRole.Owner, CreatedAt = now });
        await db.SaveChangesAsync(CancellationToken.None);

        return (inventoryId, orphanedOwnerId, targetAId, targetBId);
    }

    [Fact]
    public async Task Two_concurrent_recovery_attempts_for_the_same_orphaned_owner_never_both_succeed()
    {
        var (inventoryId, orphanedOwnerId, targetAId, targetBId) = await SeedOrphanedInventoryAsync();
        var now = DateTimeOffset.UtcNow;

        // orphanedOwnerId is deliberately absent, so both attempts' recheck resolves them as not
        // found (still orphaned); both targets resolve as active, eligible recovery destinations.
        var directory = new FixedTenantMemberDirectory(new Dictionary<Guid, ResolvedTenantMember>
        {
            [targetAId] = new(new ParticipantId(targetAId), "Target A"),
            [targetBId] = new(new ParticipantId(targetBId), "Target B"),
        });

        using var barrier = new Barrier(2);
        using var dbA = CreateContext(new SynchronizeFirstReadInterceptor(barrier));
        using var dbB = CreateContext(new SynchronizeFirstReadInterceptor(barrier));

        var storeA = new SqlInventoryRecoveryStore(dbA, directory);
        var storeB = new SqlInventoryRecoveryStore(dbB, directory);

        var identifierA = TenantMemberIdentifier.Parse(targetAId.ToString())!;
        var identifierB = TenantMemberIdentifier.Parse(targetBId.ToString())!;

        var taskA = Task.Run(() => storeA.RecoverAsync(new InventoryId(inventoryId), identifierA, "admin-a", now, CancellationToken.None));
        var taskB = Task.Run(() => storeB.RecoverAsync(new InventoryId(inventoryId), identifierB, "admin-b", now, CancellationToken.None));

        var results = await Task.WhenAll(taskA, taskB);

        Assert.Single(results, r => r.Outcome == RecoveryOutcome.Recovered);
        Assert.Single(results, r => r.Outcome == RecoveryOutcome.ConcurrentModification);

        using var verifyDb = CreateContext();
        var owners = await verifyDb.Memberships
            .AsNoTracking()
            .Where(m => m.InventoryId == inventoryId && m.Role == MembershipRole.Owner)
            .ToListAsync();

        Assert.Single(owners);
        Assert.Contains(owners.Single().ParticipantId, new[] { targetAId, targetBId });
    }

    /// <summary>Pauses the first read against <c>Memberships</c> so both recovery attempts read the current Owner row's ConcurrencyStamp before either commits.</summary>
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
