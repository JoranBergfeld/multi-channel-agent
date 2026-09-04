using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Infrastructure.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.IntegrationTests.Inventories;

/// <summary>
/// The two races Initial Import must survive, against real SQL Server - the only place they can be
/// proved, because both are about locks rather than about code. An in-memory double cannot fail these
/// tests, and SQLite has a single writer, so it cannot distinguish the guarantee from its absence.
///
/// The first is the one the whole workflow rests on: an import and an ordinary Stock write racing for
/// an empty Inventory. The empty-state assertion is a range query under serializable isolation, so
/// the two serialize; whichever loses says so, and the import is never left mixed with half of
/// somebody else's write.
///
/// The second is two confirmations of one import - two browser tabs, two requests - which carry the
/// same derived operation identity: the loser must converge on the ledger rather than tell its
/// Participant that nothing happened. A third case asks the same question of the proposal alone, so
/// its single use is proved without leaning on the ledger at all.
///
/// The seeding shape is carried here rather than shared, exactly as every shipped SQL store test
/// class carries its own.
/// </summary>
public sealed class SqlImportExecutionStoreConcurrencyTests : SqlIntegrationTestBase
{
    /// <summary>SQL Server's "Transaction was deadlocked ... and has been chosen as the deadlock victim".</summary>
    private const int DeadlockVictimErrorNumber = 1205;

    private static readonly DateTimeOffset Now = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
    private static readonly byte[] RawContent = "Name,Quantity,Unit,Location,Note\nSteel Bolts,4,,,\n"u8.ToArray();

    private readonly ParticipantId _participant = new(Guid.NewGuid());
    private readonly InventoryId _inventory = new(Guid.NewGuid());
    private readonly UnitId _unit = new(Guid.NewGuid());
    private readonly LocationId _location = new(Guid.NewGuid());

    [SkippableFact]
    public async Task An_import_racing_a_Stock_write_never_leaves_both_the_import_and_the_other_entry()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed concurrency proof.");

        await SeedAsync();
        ImportProposal proposal;
        using (var setup = NewContext())
        {
            proposal = await StorePendingAsync(setup);
        }

        async Task<ImportExecutionOutcome> ImportAsync()
        {
            using var db = NewContext();
            var result = await Store(db).ApplyAsync(Command(proposal), CancellationToken.None);
            return result.Outcome;
        }

        async Task WriteStockAsync()
        {
            using var db = NewContext();

            // Exactly the production shape: resolve, then write through the real store.
            var resolved = await new SqlInventoryReferenceStore(db).ResolveUnitAsync(
                _inventory, _unit.Value.ToString(), CancellationToken.None);

            if (resolved is null)
            {
                return;
            }

            await new SqlStockMutationStore(db).ApplyAsync(
                new StockMutationCommand
                {
                    OperationId = StockOperationId.Derive(TurnId.NewId(), "add_stock", 0),
                    InventoryId = _inventory,
                    ActorId = _participant,
                    Kind = StockMutationKind.Add,
                    Amount = Quantity.Create(1m),
                    ResultingQuantity = Quantity.Create(1m),
                    NewEntryName = "Racing Entry",
                    NewEntryUnitId = resolved,
                    NewEntryLocationId = null,
                    NotePreserved = false,
                    Now = Now,
                },
                CancellationToken.None);
        }

        // Both sides run for real. The import's empty-state assertion is a range query under
        // serializable isolation, so the two serialize - and SQL Server may legitimately pick one of
        // them as a deadlock victim, which is the isolation level working rather than a bug.
        var importTask = RunToleratingOneDeadlockAsync(ImportAsync);
        var stockTask = RunToleratingOneDeadlockAsync(WriteStockAsync);
        var (outcome, importVictim) = await importTask;
        var (_, stockVictim) = await stockTask;

        Assert.True(
            importVictim is null || stockVictim is null,
            "A deadlock has exactly one victim; both sides losing would mean something else went wrong.");

        using var verify = NewContext();
        var entries = await verify.StockEntries.AsNoTracking()
            .Where(e => e.InventoryId == _inventory.Value)
            .Select(e => e.Name)
            .ToListAsync();

        if (importVictim is not null)
        {
            // The production contract for a raw deadlock: it is never laundered into a semantic
            // answer. Nothing was applied and no ledger row exists, so the request reports a transient
            // failure and the import stays retryable, its proposal and its file untouched.
            Assert.DoesNotContain("Steel Bolts", entries);
            Assert.Empty(await verify.ImportOperations.AsNoTracking().ToListAsync());
            Assert.Equal(
                nameof(ImportProposalStatus.Pending),
                (await verify.ImportProposals.AsNoTracking().SingleAsync(p => p.ProposalId == proposal.Id.Value)).Status);
            return;
        }

        if (outcome == ImportExecutionOutcome.Applied)
        {
            // The import won, so the racing write either lost or landed after it - but the import
            // itself must be exactly what it proposed, never mixed with a half of something else.
            Assert.Contains("Steel Bolts", entries);
            Assert.Single(await verify.ImportOperations.AsNoTracking().ToListAsync());
        }
        else
        {
            Assert.Equal(ImportExecutionOutcome.Conflict, outcome);
            Assert.DoesNotContain("Steel Bolts", entries);
            Assert.Empty(await verify.ImportOperations.AsNoTracking().ToListAsync());
        }
    }

    [SkippableFact]
    public async Task Only_one_of_two_concurrent_confirmations_of_one_import_can_win()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed concurrency proof.");

        await SeedAsync();
        ImportProposal proposal;
        using (var setup = NewContext())
        {
            proposal = await StorePendingAsync(setup);
        }

        async Task<ImportExecutionOutcome> ConfirmAsync()
        {
            using var db = NewContext();
            var result = await Store(db).ApplyAsync(Command(proposal), CancellationToken.None);
            return result.Outcome;
        }

        // Exactly the production shape: two browser tabs confirming one import produce two requests
        // carrying the *same* operation identity, because the identity is derived from the proposal
        // rather than issued per request. No deadlock is tolerated here, unlike the race above - both
        // sides take the same rows in the same shared order, so a cycle between them would be the
        // avoidable kind, and a failure rather than a retry.
        var outcomes = await Task.WhenAll(ConfirmAsync(), ConfirmAsync());

        // The loser blocks on the winner's proposal row and resumes to find the proposal spent - by
        // which time its own import has been applied. Reporting a conflict there would tell a
        // Participant their import did not happen while it plainly did, so it converges on the ledger.
        Assert.Equal(1, outcomes.Count(outcome => outcome == ImportExecutionOutcome.Applied));
        Assert.Equal(1, outcomes.Count(outcome => outcome == ImportExecutionOutcome.AlreadyApplied));

        using var verify = NewContext();
        Assert.Single(await verify.StockEntries.AsNoTracking().Where(e => e.InventoryId == _inventory.Value).ToListAsync());
        Assert.Single(await verify.InventoryAudits.AsNoTracking().Where(a => a.InventoryId == _inventory.Value).ToListAsync());
        Assert.Single(await verify.ImportOperations.AsNoTracking().ToListAsync());
        Assert.Empty(await verify.ImportUploads.AsNoTracking().Where(u => u.ProposalId == proposal.Id.Value).ToListAsync());
    }

    [SkippableFact]
    public async Task One_stored_import_can_only_ever_be_consumed_once()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed concurrency proof.");

        await SeedAsync();
        ImportProposal proposal;
        using (var setup = NewContext())
        {
            proposal = await StorePendingAsync(setup);
        }

        async Task<ImportExecutionOutcome> ConfirmAsync()
        {
            using var db = NewContext();

            // A distinct operation identity per attempt, which production never produces. It is asked
            // for anyway so the proposal's own single use is proved on its own terms rather than
            // resting on the ledger's: were the identity ever to stop being derived, the guarded
            // consume - not the ledger - is what still keeps an Inventory from being imported twice.
            var command = Command(proposal) with { OperationId = new ImportOperationId(Guid.NewGuid()) };
            var result = await Store(db).ApplyAsync(command, CancellationToken.None);
            return result.Outcome;
        }

        var outcomes = await Task.WhenAll(ConfirmAsync(), ConfirmAsync());

        Assert.Equal(1, outcomes.Count(outcome => outcome == ImportExecutionOutcome.Applied));
        Assert.Equal(1, outcomes.Count(outcome => outcome == ImportExecutionOutcome.Conflict));

        using var verify = NewContext();
        Assert.Single(await verify.StockEntries.AsNoTracking().Where(e => e.InventoryId == _inventory.Value).ToListAsync());
        Assert.Single(await verify.InventoryAudits.AsNoTracking().Where(a => a.InventoryId == _inventory.Value).ToListAsync());
        Assert.Single(await verify.ImportOperations.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// Runs one side of a race, returning what it answered and the deadlock it lost to, or null when
    /// it finished. Only SQL Server error 1205 - "chosen as the deadlock victim" - is tolerated; every
    /// other fault is rethrown, so this can never quietly absorb a real failure.
    /// </summary>
    private static async Task<(ImportExecutionOutcome Outcome, SqlException? Victim)> RunToleratingOneDeadlockAsync(
        Func<Task<ImportExecutionOutcome>> side)
    {
        try
        {
            return (await side(), null);
        }
        catch (Exception exception) when (DeadlockVictim(exception) is not null)
        {
            return (default, DeadlockVictim(exception));
        }
    }

    private static async Task<(ImportExecutionOutcome Outcome, SqlException? Victim)> RunToleratingOneDeadlockAsync(
        Func<Task> side) =>
        await RunToleratingOneDeadlockAsync(async () =>
        {
            await side();
            return default;
        });

    private static SqlException? DeadlockVictim(Exception exception) => exception switch
    {
        SqlException { Number: DeadlockVictimErrorNumber } deadlock => deadlock,
        { InnerException: { } inner } => DeadlockVictim(inner),
        _ => null,
    };

    private MultiChannelAgentDbContext NewContext() =>
        Factory!.Services.CreateScope().ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();

    private static SqlImportExecutionStore Store(MultiChannelAgentDbContext db) => new(db);

    private ImportEntry Entry(string name, decimal quantity) => new()
    {
        LineNumber = 2,
        SourceLineNumbers = [2],
        Name = name,
        NormalizedName = NameNormalization.Normalize(name),
        Quantity = Quantity.Create(quantity),
        UnitId = _unit,
        UnitCanonicalName = "each",
        LocationId = null,
        LocationName = null,
        Note = null,
    };

    private ImportExecutionCommand Command(ImportProposal proposal) => new()
    {
        OperationId = proposal.ExecutionOperationId,
        InventoryId = _inventory,
        ActorId = _participant,
        ConsumesProposalId = proposal.Id,
        FileDigest = proposal.FileDigest,
        Entries = proposal.Entries,
        EmptyStateVersion = proposal.EmptyStateVersion,
        Now = Now,
    };

    private async Task<ImportProposal> StorePendingAsync(MultiChannelAgentDbContext db)
    {
        var proposal = ImportProposal.Create(
            ConfirmationToken.HashOf(ConfirmationToken.Issue()),
            _participant,
            _inventory,
            FileDigest.Of(RawContent),
            [Entry("Steel Bolts", 4m)],
            EmptyStateVersion.Empty,
            Now);

        await new SqlImportProposalStore(db).StoreAsync(proposal, RawContent, Now, CancellationToken.None);
        db.ChangeTracker.Clear();

        return proposal;
    }

    private async Task SeedAsync()
    {
        using var db = NewContext();

        db.Participants.Add(new ParticipantEntity
        {
            Id = _participant.Value,
            DisplayName = "Owner Person",
            CreatedAt = Now,
            UpdatedAt = Now,
        });
        db.Inventories.Add(new InventoryEntity
        {
            Id = _inventory.Value,
            Name = $"Warehouse {_inventory.Value:N}",
            NormalizedName = $"warehouse {_inventory.Value:N}",
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
        db.Locations.Add(new LocationEntity
        {
            Id = _location.Value,
            InventoryId = _inventory.Value,
            Name = "Shelf A",
            NormalizedName = "shelf a",
            ConcurrencyStamp = Guid.NewGuid(),
            CreatedAt = Now,
        });

        await db.SaveChangesAsync();
    }
}
