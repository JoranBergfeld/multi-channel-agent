using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Infrastructure.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;
using MultiChannelAgent.Infrastructure.Persistence.Migrations;

namespace MultiChannelAgent.IntegrationTests.Inventories;

/// <summary>
/// Fast, Docker-free coverage of the one thing #33's migration has to do for data that already
/// exists: settle every proposal that was Pending when it ran.
///
/// From the moment this migration lands, "a confirmed Retire invalidates every pending proposal that
/// references the retired identity" is implemented by joining <c>ConfirmationProposalReferences</c> -
/// a table that is written when a proposal is stored, and which therefore holds nothing at all for a
/// proposal stored before the deploy. Such a proposal would be invisible to every Retire and could
/// still be confirmed afterwards, because <c>SqlStockChangeSetStore</c> pins Stock Entry versions and
/// never independently checks whether a Unit or Location has since been retired.
///
/// Reconstructing the index from legacy payloads would mean parsing every historical serialized shape
/// and hoping none was malformed. A proposal lives ten minutes, so the conservative answer is simply
/// to settle them all: nothing a Participant reviewed is executed against a state nobody can vouch
/// for, and they are told only that there is nothing waiting for them.
/// </summary>
public sealed class ReferenceAdministrationMigrationTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly MultiChannelAgentDbContext _db;
    private readonly ParticipantId _participantId = new(Guid.NewGuid());
    private readonly Guid _inventoryId = Guid.NewGuid();
    private readonly Guid _unitId = Guid.NewGuid();

    public ReferenceAdministrationMigrationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _db = new MultiChannelAgentDbContext(
            new DbContextOptionsBuilder<MultiChannelAgentDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        Seed();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public void The_migration_settles_every_pending_proposal_before_it_builds_the_reference_index()
    {
        var operations = new AddReferenceAdministration().UpOperations.ToList();

        var settleIndex = operations.FindIndex(IsPendingProposalSettle);
        Assert.True(settleIndex >= 0, "The migration must settle every Pending proposal.");

        var referenceIndexTable = operations.FindIndex(operation =>
            operation is CreateTableOperation { Name: "ConfirmationProposalReferences" });
        Assert.True(referenceIndexTable >= 0);

        // Ordered first, so the guarded update runs against exactly the schema those proposals were
        // written under - and so no proposal can ever be reachable by the new reference-index contract
        // without having an index row of its own.
        Assert.True(
            settleIndex < referenceIndexTable,
            "Pending proposals must be settled before the reference index the new contract joins exists.");
    }

    [Fact]
    public void The_settle_is_guarded_so_it_can_never_move_an_already_terminal_proposal()
    {
        var sql = SettleSql();

        Assert.Contains("WHERE Status = 'Pending'", sql, StringComparison.Ordinal);
        Assert.Contains($"Status = '{nameof(ProposalStatus.Conflicted)}'", sql, StringComparison.Ordinal);

        // Retention is measured from SettledAt and the expiry sweep orders by SettledAtTicks, so a
        // settled row that carried neither would never be swept.
        Assert.Contains("SettledAt =", sql, StringComparison.Ordinal);
        Assert.Contains("SettledAtTicks =", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Down_does_not_resurrect_an_invalidated_proposal()
    {
        var down = new AddReferenceAdministration().DownOperations;

        Assert.DoesNotContain(down, IsPendingProposalSettle);
        Assert.DoesNotContain(
            down,
            operation => operation is SqlOperation sql
                && sql.Sql.Contains($"'{nameof(ProposalStatus.Pending)}'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_proposal_that_was_pending_when_the_migration_ran_is_no_longer_confirmable()
    {
        var store = new SqlConfirmationProposalStore(_db);
        var proposal = StockProposal();
        await store.StoreAsync(proposal, Now, CancellationToken.None);

        Assert.NotNull(await store.FindPendingAsync(_participantId, proposal.ChannelConversationId, CancellationToken.None));

        await RunSettleAsync();

        Assert.Null(await store.FindPendingAsync(_participantId, proposal.ChannelConversationId, CancellationToken.None));
        Assert.Equal(ProposalStatus.Conflicted, await store.FindStatusAsync(proposal.Id, CancellationToken.None));

        var row = await _db.ConfirmationProposals.AsNoTracking().SingleAsync(p => p.ProposalId == proposal.Id.Value);
        Assert.NotNull(row.SettledAt);
        Assert.NotNull(row.SettledAtTicks);
    }

    [Fact]
    public async Task A_proposal_the_migration_settled_can_never_be_stranded_by_a_later_Retire()
    {
        var store = new SqlConfirmationProposalStore(_db);
        var proposal = StockProposal();
        await store.StoreAsync(proposal, Now, CancellationToken.None);

        // The index row this proposal happens to have is deliberately removed, reproducing exactly what
        // a pre-deploy proposal looks like: a row no Retire can see.
        await _db.ConfirmationProposalReferences
            .Where(r => r.ProposalId == proposal.Id.Value)
            .ExecuteDeleteAsync();

        await RunSettleAsync();

        // Retiring the Unit it depended on finds nothing to settle - because the migration already did.
        Assert.Equal(
            0,
            await store.InvalidateReferencingAsync(
                new InventoryId(_inventoryId), ReferenceKind.Unit, _unitId, Now, CancellationToken.None));

        // And it is still terminal: nothing ever returns a proposal to Pending.
        Assert.Equal(ProposalStatus.Conflicted, await store.FindStatusAsync(proposal.Id, CancellationToken.None));
        Assert.Null(await store.FindPendingAsync(_participantId, proposal.ChannelConversationId, CancellationToken.None));
    }

    [Fact]
    public async Task A_terminal_proposal_is_left_exactly_where_it_was()
    {
        var store = new SqlConfirmationProposalStore(_db);
        var proposal = StockProposal();
        await store.StoreAsync(proposal, Now, CancellationToken.None);
        await store.SettleAsync(proposal.Id, ProposalStatus.Rejected, Now, CancellationToken.None);

        await RunSettleAsync();

        Assert.Equal(ProposalStatus.Rejected, await store.FindStatusAsync(proposal.Id, CancellationToken.None));
    }

    /// <summary>
    /// Runs the migration's own settle statement - taken from the migration, never copied - so this
    /// test can never pass against a statement the migration does not actually carry.
    /// </summary>
    private async Task RunSettleAsync()
    {
        await _db.Database.ExecuteSqlRawAsync(SettleSql());
        _db.ChangeTracker.Clear();
    }

    private static string SettleSql() =>
        Assert.IsType<SqlOperation>(new AddReferenceAdministration().UpOperations.Single(IsPendingProposalSettle)).Sql;

    private static bool IsPendingProposalSettle(MigrationOperation operation) =>
        operation is SqlOperation sql
        && sql.Sql.Contains("UPDATE ConfirmationProposals", StringComparison.Ordinal)
        && sql.Sql.Contains($"'{nameof(ProposalStatus.Pending)}'", StringComparison.Ordinal);

    private ConfirmationProposal StockProposal()
    {
        var stockEntryId = new StockEntryId(Guid.NewGuid());

        return ConfirmationProposal.Create(
            ConfirmationToken.HashOf(ConfirmationToken.Issue()),
            _participantId,
            "web:profile-1",
            new InventoryId(_inventoryId),
            TurnId.NewId(),
            [
                new ProposedChange
                {
                    Order = 1,
                    Kind = StockMutationKind.Forget,
                    Effect = StockChangeEffectKind.Forgotten,
                    Source = new ProposedEntryState(
                        stockEntryId, "Steel Bolts", "steel bolts", new UnitId(_unitId), "each",
                        LocationId: null, LocationName: null, Note: null,
                        Quantity.Zero, Quantity.Zero, Retired: true),
                },
            ],
            [new ExpectedEntryVersion(stockEntryId, Guid.NewGuid())],
            [],
            Now);
    }

    private void Seed()
    {
        _db.Participants.Add(new ParticipantEntity
        {
            Id = _participantId.Value,
            DisplayName = "Owner Person",
            CreatedAt = Now,
            UpdatedAt = Now,
        });
        _db.Inventories.Add(new InventoryEntity
        {
            Id = _inventoryId,
            Name = "Warehouse",
            NormalizedName = "warehouse",
            CreatedByParticipantId = _participantId.Value,
            ClientRequestId = "seed-1",
            CreatedAt = Now,
        });
        _db.Units.Add(new UnitEntity
        {
            Id = _unitId,
            InventoryId = _inventoryId,
            CanonicalName = "each",
            NormalizedCanonicalName = "each",
            IsReserved = true,
            ConcurrencyStamp = Guid.NewGuid(),
            CreatedAt = Now,
        });
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
    }
}
