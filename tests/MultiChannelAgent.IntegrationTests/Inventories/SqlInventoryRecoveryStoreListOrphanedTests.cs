using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.IntegrationTests.Inventories;

/// <summary>
/// Fast, Docker-free coverage for two properties <see cref="SqlInventoryRecoveryStore.ListOrphanedAsync"/>
/// must guarantee beyond the recovery race already covered by
/// <see cref="SqlInventoryRecoveryStoreConcurrencyTests"/>: (1) resolving each distinct Owner's active
/// status against the tenant directory is bounded-concurrent rather than one-at-a-time, so listing
/// does not degrade linearly as the number of Owners grows; and (2) a tenant directory outage - a
/// typed <see cref="TenantDirectoryUnavailableException"/>, exactly what a real Microsoft Graph
/// authorization/transient failure now throws instead of silently resolving nobody - propagates
/// straight out of the listing call rather than ever being swallowed into "this Owner is orphaned",
/// and never persists a stale/incorrect <see cref="ParticipantEntity.IsActive"/> flag for an Owner
/// whose real, healthy status the outage prevented reconfirming.
/// </summary>
public sealed class SqlInventoryRecoveryStoreListOrphanedTests : IDisposable
{
    private readonly SqliteConnection _keepAliveConnection;
    private readonly string _connectionString;

    public SqlInventoryRecoveryStoreListOrphanedTests()
    {
        _connectionString = $"DataSource=file:{Guid.NewGuid()}?mode=memory&cache=shared";
        _keepAliveConnection = new SqliteConnection(_connectionString);
        _keepAliveConnection.Open();

        using var db = CreateContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _keepAliveConnection.Dispose();

    private MultiChannelAgentDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<MultiChannelAgentDbContext>().UseSqlite(_connectionString).Options);

    private async Task<Guid> SeedOwnedInventoryAsync(MultiChannelAgentDbContext db, string label, bool ownerCurrentlyActive)
    {
        var inventoryId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.Participants.Add(new ParticipantEntity { Id = ownerId, DisplayName = $"{label} Owner", IsActive = ownerCurrentlyActive, CreatedAt = now, UpdatedAt = now });
        db.Inventories.Add(new InventoryEntity
        {
            Id = inventoryId,
            Name = $"{label} Warehouse",
            NormalizedName = $"{label.ToLowerInvariant()} warehouse",
            CreatedByParticipantId = ownerId,
            ClientRequestId = $"seed-{label}-{inventoryId}",
            CreatedAt = now,
        });
        db.Memberships.Add(new MembershipEntity { InventoryId = inventoryId, ParticipantId = ownerId, Role = MembershipRole.Owner, CreatedAt = now });
        await db.SaveChangesAsync(CancellationToken.None);

        return ownerId;
    }

    /// <summary>Tracks how many <see cref="ResolveAsync"/> calls are in flight at once, so a test can assert on peak concurrency without any real network/timing flakiness beyond a fixed artificial delay.</summary>
    private sealed class ConcurrencyTrackingDirectory : ITenantMemberDirectory
    {
        private readonly object _gate = new();
        private int _inFlight;

        public int MaxObservedConcurrency { get; private set; }

        public async Task<ResolvedTenantMember?> ResolveAsync(TenantMemberIdentifier identifier, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _inFlight++;
                MaxObservedConcurrency = Math.Max(MaxObservedConcurrency, _inFlight);
            }

            await Task.Delay(25, cancellationToken);

            lock (_gate)
            {
                _inFlight--;
            }

            return null;
        }
    }

    [Fact]
    public async Task ListOrphanedAsync_resolves_many_distinct_owners_with_bounded_concurrency_not_one_at_a_time()
    {
        using var db = CreateContext();
        for (var i = 0; i < 20; i++)
        {
            await SeedOwnedInventoryAsync(db, $"Inv{i}", ownerCurrentlyActive: true);
        }

        var directory = new ConcurrencyTrackingDirectory();
        var store = new SqlInventoryRecoveryStore(db, directory);

        await store.ListOrphanedAsync(maxResults: 100, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.True(directory.MaxObservedConcurrency > 1, "Expected owner resolution to run concurrently, not strictly one at a time.");
        Assert.True(directory.MaxObservedConcurrency <= 8, $"Expected bounded concurrency (<= 8 in flight), but observed {directory.MaxObservedConcurrency}.");
    }

    /// <summary>A directory double simulating a total tenant directory outage: every resolution throws, exactly like a real Microsoft Graph 401/403/5xx/network failure now does instead of silently returning "not found".</summary>
    private sealed class AlwaysUnavailableDirectory : ITenantMemberDirectory
    {
        public Task<ResolvedTenantMember?> ResolveAsync(TenantMemberIdentifier identifier, CancellationToken cancellationToken) =>
            throw new TenantDirectoryUnavailableException("Simulated Microsoft Graph outage.");
    }

    [Fact]
    public async Task A_directory_outage_propagates_and_never_falsely_marks_a_healthy_owner_orphaned_or_inactive()
    {
        Guid healthyOwnerId;
        using (var seedDb = CreateContext())
        {
            healthyOwnerId = await SeedOwnedInventoryAsync(seedDb, "Healthy", ownerCurrentlyActive: true);
        }

        using var db = CreateContext();
        var store = new SqlInventoryRecoveryStore(db, new AlwaysUnavailableDirectory());

        await Assert.ThrowsAsync<TenantDirectoryUnavailableException>(
            () => store.ListOrphanedAsync(maxResults: 100, DateTimeOffset.UtcNow, CancellationToken.None));

        using var verifyDb = CreateContext();
        var owner = await verifyDb.Participants.AsNoTracking().SingleAsync(p => p.Id == healthyOwnerId);
        Assert.True(owner.IsActive, "A directory outage must never flip a healthy owner's persisted IsActive to false.");
    }
}
