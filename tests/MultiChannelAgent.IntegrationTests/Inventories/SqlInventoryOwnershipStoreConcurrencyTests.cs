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
/// Fast, Docker-free regression coverage for the atomicity <see cref="SqlInventoryOwnershipStore"/>
/// must guarantee under real contention: two independent <see cref="MultiChannelAgentDbContext"/>
/// instances - each its own real SQLite connection into one shared-cache in-memory database,
/// mirroring two separate HTTP request scopes - both attempt to transfer ownership of the SAME
/// Inventory away from the SAME current Owner to two different targets at the same instant. Exactly
/// one may commit; the loser must report <see cref="TransferOutcome.ConcurrentModification"/> rather
/// than either silently succeeding or leaking a raw <see cref="DbUpdateConcurrencyException"/> - and
/// the Inventory must end up with exactly one Owner, never zero, never two.
/// </summary>
public sealed class SqlInventoryOwnershipStoreConcurrencyTests : IDisposable
{
    private readonly SqliteConnection _keepAliveConnection;
    private readonly string _connectionString;

    public SqlInventoryOwnershipStoreConcurrencyTests()
    {
        _connectionString = $"DataSource=file:{Guid.NewGuid()}?mode=memory&cache=shared";
        _keepAliveConnection = new SqliteConnection(_connectionString);
        _keepAliveConnection.Open();

        using var db = CreateContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _keepAliveConnection.Dispose();

    private async Task<(Guid InventoryId, Guid OwnerId, Guid TargetAId, Guid TargetBId)> SeedInventoryWithOwnerAndTwoTargetsAsync()
    {
        var inventoryId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var targetAId = Guid.NewGuid();
        var targetBId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        using var db = CreateContext();
        db.Participants.Add(new ParticipantEntity { Id = ownerId, DisplayName = "Owner", CreatedAt = now, UpdatedAt = now });
        db.Participants.Add(new ParticipantEntity { Id = targetAId, DisplayName = "Target A", CreatedAt = now, UpdatedAt = now });
        db.Participants.Add(new ParticipantEntity { Id = targetBId, DisplayName = "Target B", CreatedAt = now, UpdatedAt = now });
        db.Inventories.Add(new InventoryEntity
        {
            Id = inventoryId,
            Name = "Race Warehouse",
            NormalizedName = "race warehouse",
            CreatedByParticipantId = ownerId,
            ClientRequestId = "seed-race",
            CreatedAt = now,
        });
        db.Memberships.Add(new MembershipEntity { InventoryId = inventoryId, ParticipantId = ownerId, Role = MembershipRole.Owner, CreatedAt = now });
        await db.SaveChangesAsync(CancellationToken.None);

        return (inventoryId, ownerId, targetAId, targetBId);
    }

    [Fact]
    public async Task Two_concurrent_transfer_attempts_for_the_same_owner_never_both_succeed()
    {
        var (inventoryId, ownerId, targetAId, targetBId) = await SeedInventoryWithOwnerAndTwoTargetsAsync();
        var now = DateTimeOffset.UtcNow;

        using var barrier = new Barrier(2);
        using var dbA = CreateContext(new SynchronizeFirstReadInterceptor(barrier));
        using var dbB = CreateContext(new SynchronizeFirstReadInterceptor(barrier));

        var storeA = new SqlInventoryOwnershipStore(dbA);
        var storeB = new SqlInventoryOwnershipStore(dbB);

        var taskA = Task.Run(() => storeA.TransferAsync(
            new InventoryId(inventoryId), new ParticipantId(ownerId), new ParticipantId(targetAId), "Target A", now, CancellationToken.None));
        var taskB = Task.Run(() => storeB.TransferAsync(
            new InventoryId(inventoryId), new ParticipantId(ownerId), new ParticipantId(targetBId), "Target B", now, CancellationToken.None));

        var results = await Task.WhenAll(taskA, taskB);

        Assert.Single(results, r => r.Outcome == TransferOutcome.Transferred);
        Assert.Single(results, r => r.Outcome == TransferOutcome.ConcurrentModification);

        using var verifyDb = CreateContext();
        var owners = await verifyDb.Memberships
            .AsNoTracking()
            .Where(m => m.InventoryId == inventoryId && m.Role == MembershipRole.Owner)
            .ToListAsync();

        Assert.Single(owners);
        Assert.Contains(owners.Single().ParticipantId, new[] { targetAId, targetBId });
    }

    [Fact]
    public async Task Transfer_racing_a_grant_for_the_same_new_target_reports_one_insert_conflict()
    {
        var (inventoryId, ownerId, targetId, _) = await SeedInventoryWithOwnerAndTwoTargetsAsync();
        var now = DateTimeOffset.UtcNow;

        using var barrier = new Barrier(2);
        using var grantDb = CreateContext(new SynchronizeMembershipReadInterceptor(barrier, readNumber: 1));
        using var transferDb = CreateContext(new SynchronizeMembershipReadInterceptor(barrier, readNumber: 2));
        var membershipStore = new SqlInventoryMembershipStore(grantDb);
        var ownershipStore = new SqlInventoryOwnershipStore(transferDb);

        var grantTask = Task.Run(() => membershipStore.GrantOrChangeRoleAsync(
            new InventoryId(inventoryId), new ParticipantId(ownerId), new ParticipantId(targetId),
            "Target A", MembershipRole.Viewer, now, CancellationToken.None));
        var transferTask = Task.Run(() => ownershipStore.TransferAsync(
            new InventoryId(inventoryId), new ParticipantId(ownerId), new ParticipantId(targetId),
            "Target A", now, CancellationToken.None));

        var grant = await grantTask;
        var transfer = await transferTask;

        Assert.True(
            grant.Outcome == MembershipGrantOutcome.Granted
                && transfer.Outcome == TransferOutcome.ConcurrentModification
            || grant.Outcome == MembershipGrantOutcome.ConcurrentModification
                && transfer.Outcome == TransferOutcome.Transferred);

        using var verifyDb = CreateContext();
        Assert.Single(await verifyDb.Memberships.AsNoTracking()
            .Where(membership => membership.InventoryId == inventoryId && membership.ParticipantId == targetId)
            .ToListAsync());
        Assert.Single(await verifyDb.Memberships.AsNoTracking()
            .Where(membership => membership.InventoryId == inventoryId && membership.Role == MembershipRole.Owner)
            .ToListAsync());
        Assert.Single(await verifyDb.InventoryAudits.AsNoTracking()
            .Where(audit => audit.InventoryId == inventoryId)
            .ToListAsync());
    }

    /// <summary>
    /// Pauses the very first read against <c>Memberships</c> issued through this interceptor's
    /// <see cref="MultiChannelAgentDbContext"/> until a second participant (the other concurrent
    /// attempt's own interceptor instance, sharing the same <see cref="Barrier"/>) reaches the same
    /// point - forcing both transfer attempts to read the current Owner row's ConcurrencyStamp before
    /// either one commits, deterministically reproducing the race.
    /// </summary>
    private sealed class SynchronizeFirstReadInterceptor(Barrier checkArrivalBarrier) : DbCommandInterceptor
    {
        private bool _synchronized;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (!_synchronized && command.CommandText.Contains("Memberships", StringComparison.Ordinal))
            {
                _synchronized = true;
                await Task.Run(() => checkArrivalBarrier.SignalAndWait(cancellationToken), cancellationToken);
            }

            return await base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class SynchronizeMembershipReadInterceptor(Barrier checkArrivalBarrier, int readNumber) : DbCommandInterceptor
    {
        private int _membershipReadCount;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("Memberships", StringComparison.Ordinal)
                && Interlocked.Increment(ref _membershipReadCount) == readNumber)
            {
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
