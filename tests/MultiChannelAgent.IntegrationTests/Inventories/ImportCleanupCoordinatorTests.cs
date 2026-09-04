using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.IntegrationTests.Inventories;

/// <summary>
/// Docker-free coverage that the cleanup this ticket promises actually happens against a real
/// relational engine, in the shape <see cref="ConfirmationExpirySqliteTests"/> established: an
/// in-memory SQLite database, controlled time, and the registered coordinator over the registered
/// stores - never a double, because the facts at stake are durable ones. A pending import's ten
/// minutes end it and take its raw file with it, a settled one is discarded once it is past
/// retention, an audit fact outliving <see cref="AuditFact.RetentionDays"/> is swept, and a replica
/// that cannot take the lease does none of it.
/// </summary>
public sealed class ImportCleanupCoordinatorTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
    private static readonly byte[] RawContent = "Name,Quantity,Unit,Location,Note\nSteel Bolts,10.5,,,\n"u8.ToArray();

    private readonly FakeTimeProvider _time = new(Now);
    private readonly ParticipantId _participant = new(Guid.NewGuid());
    private readonly InventoryId _inventory = new(Guid.NewGuid());
    private readonly UnitId _unit = new(Guid.NewGuid());

    private SqliteWebApplicationFactory _factory = null!;
    private IServiceScope _scope = null!;
    private IServiceScope? _otherReplicaScope;
    private MultiChannelAgentDbContext _db = null!;
    private IImportProposalStore _proposals = null!;

    public async Task InitializeAsync()
    {
        _factory = new SqliteWebApplicationFactory(_time);
        _scope = _factory.Services.CreateScope();
        _db = _scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        _proposals = _scope.ServiceProvider.GetRequiredService<IImportProposalStore>();

        await SeedAsync();
    }

    public Task DisposeAsync()
    {
        _otherReplicaScope?.Dispose();
        _scope.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task An_expired_import_leaves_Pending_and_its_file_is_discarded()
    {
        var proposal = await StorePendingAsync();
        _time.SetUtcNow(Now.AddMinutes(ImportProposal.LifetimeMinutes));

        Assert.Equal(1, await Coordinator().SweepAsync(CancellationToken.None));

        Assert.Equal(
            ImportProposalStatus.Expired,
            await _proposals.FindStatusAsync(proposal.Id, CancellationToken.None));
        Assert.Null(await _proposals.FindRawContentAsync(proposal.Id, CancellationToken.None));
    }

    [Fact]
    public async Task A_still_pending_import_is_left_exactly_where_it_is()
    {
        var proposal = await StorePendingAsync();
        _time.SetUtcNow(Now.AddMinutes(ImportProposal.LifetimeMinutes - 1));

        await Coordinator().SweepAsync(CancellationToken.None);

        Assert.Equal(ImportProposalStatus.Pending, await _proposals.FindStatusAsync(proposal.Id, CancellationToken.None));
        Assert.NotNull(await _proposals.FindRawContentAsync(proposal.Id, CancellationToken.None));
    }

    [Fact]
    public async Task A_settled_import_is_discarded_once_it_is_past_retention()
    {
        var proposal = await StorePendingAsync();
        await _proposals.SettleAsync(proposal.Id, ImportProposalStatus.Rejected, Now, CancellationToken.None);
        _time.SetUtcNow(Now + ImportCleanupCoordinator.SettledRetention + TimeSpan.FromMinutes(1));

        await Coordinator().SweepAsync(CancellationToken.None);

        Assert.Null(await _proposals.FindStatusAsync(proposal.Id, CancellationToken.None));
    }

    [Fact]
    public async Task An_audit_fact_older_than_ninety_days_is_discarded_and_a_newer_one_is_kept()
    {
        await SeedAuditAsync(Now.AddDays(-AuditFact.RetentionDays).AddMinutes(-1));
        await SeedAuditAsync(Now.AddDays(-AuditFact.RetentionDays).AddMinutes(1));

        await Coordinator().SweepAsync(CancellationToken.None);

        var survivor = Assert.Single(await _db.InventoryAudits.AsNoTracking().ToListAsync());
        Assert.Equal(Now.AddDays(-AuditFact.RetentionDays).AddMinutes(1), survivor.OccurredAtUtc);
    }

    [Fact]
    public async Task A_sweep_that_cannot_take_the_lease_does_nothing_at_all()
    {
        var proposal = await StorePendingAsync();
        _time.SetUtcNow(Now.AddMinutes(ImportProposal.LifetimeMinutes));

        // A real lease held by another replica, not a double told to say no: the point of the lease
        // is that the database refuses the second holder, so that is what this drives.
        await using var heldElsewhere = await HoldLeaseElsewhereAsync();
        Assert.NotNull(heldElsewhere);

        Assert.Equal(0, await Coordinator().SweepAsync(CancellationToken.None));
        Assert.Equal(ImportProposalStatus.Pending, await _proposals.FindStatusAsync(proposal.Id, CancellationToken.None));
    }

    private ImportCleanupCoordinator Coordinator() =>
        _scope.ServiceProvider.GetRequiredService<ImportCleanupCoordinator>();

    private async Task<ILeaseHandle?> HoldLeaseElsewhereAsync()
    {
        // The holding replica's scope outlives the handle it returns: releasing the lease goes back
        // through that scope's DbContext.
        _otherReplicaScope = _factory.Services.CreateScope();
        return await _otherReplicaScope.ServiceProvider.GetRequiredService<ILeaseCoordinator>()
            .TryAcquireAsync("import-cleanup", "another-replica", TimeSpan.FromSeconds(30), CancellationToken.None);
    }

    private async Task<ImportProposal> StorePendingAsync()
    {
        var proposal = ImportProposal.Create(
            ConfirmationToken.HashOf(ConfirmationToken.Issue()),
            _participant,
            _inventory,
            FileDigest.Of(RawContent),
            [
                new ImportEntry
                {
                    LineNumber = 2,
                    SourceLineNumbers = [2],
                    Name = "Steel Bolts",
                    NormalizedName = NameNormalization.Normalize("Steel Bolts"),
                    Quantity = Quantity.Create(10.5m),
                    UnitId = _unit,
                    UnitCanonicalName = "each",
                    LocationId = null,
                    LocationName = null,
                    Note = null,
                },
            ],
            EmptyStateVersion.Empty,
            Now);

        await _proposals.StoreAsync(proposal, RawContent, Now, CancellationToken.None);
        return proposal;
    }

    /// <summary>
    /// Records the fact through the production audit store, so a sweep that only works because a test
    /// hand-wrote a column production never fills cannot pass here.
    /// </summary>
    private async Task SeedAuditAsync(DateTimeOffset occurredAt)
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IInventoryAuthorizationAuditStore>().RecordDenialAsync(
            AuditFact.Create(
                AuditEventType.AccessDenied,
                AuditActorKind.Participant,
                _participant.Value.ToString(),
                _inventory,
                subjectParticipantId: null,
                "Denied:NotAMember",
                occurredAt),
            clearSelectionParticipantId: null,
            clearSelectionChannelConversationId: null,
            CancellationToken.None);
    }

    private async Task SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();

        db.Participants.Add(new ParticipantEntity
        {
            Id = _participant.Value,
            DisplayName = "Importing Owner",
            CreatedAt = Now,
            UpdatedAt = Now,
        });
        db.Inventories.Add(new InventoryEntity
        {
            Id = _inventory.Value,
            Name = "Cleanup Warehouse",
            NormalizedName = "cleanup warehouse",
            CreatedByParticipantId = _participant.Value,
            ClientRequestId = Guid.NewGuid().ToString(),
            CreatedAt = Now,
        });
        db.Units.Add(new UnitEntity
        {
            Id = _unit.Value,
            InventoryId = _inventory.Value,
            CanonicalName = "each",
            NormalizedCanonicalName = "each",
            IsReserved = true,
            ConcurrencyStamp = Guid.NewGuid(),
            CreatedAt = Now,
        });

        await db.SaveChangesAsync();
    }
}
