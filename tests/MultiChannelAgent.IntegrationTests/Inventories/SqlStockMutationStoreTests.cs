using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.IntegrationTests.Inventories;

/// <summary>
/// Fast, Docker-free proof against a real relational engine of the four properties a stock mutation
/// must have: the Stock Entry change, its audit fact, and its ledger row commit together or not at
/// all; the same operation identity never applies twice; a target that moved since the caller planned
/// is refused rather than overwritten; and two concurrent creates of the same Equivalent Stock cannot
/// both land.
/// </summary>
public sealed class SqlStockMutationStoreTests : IDisposable
{
    private readonly SqliteConnection _keepAliveConnection;
    private readonly string _connectionString;
    private readonly Guid _inventoryId = Guid.NewGuid();
    private readonly Guid _unitId = Guid.NewGuid();
    private readonly ParticipantId _actorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public SqlStockMutationStoreTests()
    {
        _connectionString = $"DataSource=file:{Guid.NewGuid()}?mode=memory&cache=shared";
        _keepAliveConnection = new SqliteConnection(_connectionString);
        _keepAliveConnection.Open();

        using var db = CreateContext();
        db.Database.EnsureCreated();
        Seed(db);
    }

    public void Dispose() => _keepAliveConnection.Dispose();

    [Fact]
    public async Task Creating_stock_writes_the_entry_its_audit_fact_and_its_ledger_row_together()
    {
        using var db = CreateContext();

        var result = await new SqlStockMutationStore(db).ApplyAsync(CreateCommand(), CancellationToken.None);

        Assert.Equal(StockMutationStoreOutcome.Applied, result.Outcome);
        Assert.True(result.Recorded!.CreatedEntry);
        Assert.Equal("12.5", result.Recorded.ResultingQuantity.ToInvariantText());
        Assert.Equal("each", result.Recorded.UnitCanonicalName);

        using var reader = CreateContext();
        var entry = Assert.Single(reader.StockEntries.AsNoTracking().Where(e => e.InventoryId == _inventoryId));
        Assert.Equal(12.5m, entry.Quantity);
        Assert.Single(reader.StockOperations.AsNoTracking());
        var fact = Assert.Single(reader.InventoryAudits.AsNoTracking().Where(a => a.EventType == "StockAdded"));
        Assert.Equal("Add:Created", fact.OutcomeCode);
        Assert.Null(fact.SubjectParticipantId);
        Assert.Equal(_actorId.ToString(), fact.ActorId);
    }

    [Fact]
    public async Task Applying_the_same_operation_identity_again_re_reports_it_and_changes_nothing()
    {
        var command = CreateCommand();

        using (var db = CreateContext())
        {
            await new SqlStockMutationStore(db).ApplyAsync(command, CancellationToken.None);
        }

        using var retryContext = CreateContext();
        var retry = await new SqlStockMutationStore(retryContext).ApplyAsync(command, CancellationToken.None);

        Assert.Equal(StockMutationStoreOutcome.AlreadyApplied, retry.Outcome);
        Assert.Equal("12.5", retry.Recorded!.ResultingQuantity.ToInvariantText());

        using var reader = CreateContext();
        Assert.Equal(12.5m, Assert.Single(reader.StockEntries.AsNoTracking()).Quantity);
        Assert.Single(reader.StockOperations.AsNoTracking());
        Assert.Single(reader.InventoryAudits.AsNoTracking().Where(a => a.EventType == "StockAdded"));
    }

    [Fact]
    public async Task A_target_whose_Quantity_changed_since_the_caller_planned_is_refused_outright()
    {
        var entryId = SeedStock("Steel Bolts", 10m);

        // A competing writer commits first.
        using (var competitor = CreateContext())
        {
            var row = competitor.StockEntries.Single(e => e.Id == entryId);
            row.Quantity = 4m;
            row.ConcurrencyStamp = Guid.NewGuid();
            await competitor.SaveChangesAsync();
        }

        using var db = CreateContext();
        var result = await new SqlStockMutationStore(db).ApplyAsync(
            UpdateCommand(entryId, expected: 10m, resulting: 15m), CancellationToken.None);

        Assert.Equal(StockMutationStoreOutcome.StateChanged, result.Outcome);
        Assert.Null(result.Recorded);

        using var reader = CreateContext();
        Assert.Equal(4m, reader.StockEntries.AsNoTracking().Single(e => e.Id == entryId).Quantity);
        Assert.Empty(reader.StockOperations.AsNoTracking());
        Assert.Empty(reader.InventoryAudits.AsNoTracking().Where(a => a.EventType == "StockAdded"));
    }

    [Fact]
    public async Task Two_creates_of_the_same_Equivalent_Stock_cannot_both_land()
    {
        using (var first = CreateContext())
        {
            await new SqlStockMutationStore(first).ApplyAsync(CreateCommand(), CancellationToken.None);
        }

        using var second = CreateContext();
        var result = await new SqlStockMutationStore(second).ApplyAsync(
            CreateCommand(operationId: new StockOperationId(Guid.NewGuid())), CancellationToken.None);

        Assert.Equal(StockMutationStoreOutcome.StateChanged, result.Outcome);

        using var reader = CreateContext();
        Assert.Single(reader.StockEntries.AsNoTracking().Where(e => e.NormalizedName == "steel bolts"));
    }

    // The window the concurrency stamp exists to close: two callers both read the same Quantity, both
    // decide, and both try to save. Exactly one may win; the loser must change nothing.
    [Fact]
    public async Task Two_callers_that_both_read_the_same_Quantity_cannot_both_apply()
    {
        var entryId = SeedStock("Steel Bolts", 10m);

        using var firstContext = CreateContext();
        using var secondContext = CreateContext();

        // Both load the row (and so both hold the same concurrency stamp) before either saves.
        _ = await firstContext.StockEntries.FirstAsync(e => e.Id == entryId);
        _ = await secondContext.StockEntries.FirstAsync(e => e.Id == entryId);

        var first = await new SqlStockMutationStore(firstContext).ApplyAsync(
            UpdateCommand(entryId, expected: 10m, resulting: 15m), CancellationToken.None);

        var second = await new SqlStockMutationStore(secondContext).ApplyAsync(
            SecondUpdateCommand(entryId, expected: 10m, resulting: 12m), CancellationToken.None);

        Assert.Equal(StockMutationStoreOutcome.Applied, first.Outcome);
        Assert.Equal(StockMutationStoreOutcome.StateChanged, second.Outcome);

        using var reader = CreateContext();
        Assert.Equal(15m, reader.StockEntries.AsNoTracking().Single(e => e.Id == entryId).Quantity);
        Assert.Single(reader.StockOperations.AsNoTracking());
        Assert.Single(reader.InventoryAudits.AsNoTracking().Where(a => a.EventType == "StockAdded"));
    }

    private StockMutationCommand SecondUpdateCommand(Guid entryId, decimal expected, decimal resulting) =>
        UpdateCommand(entryId, expected, resulting) with
        {
            OperationId = new StockOperationId(Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee")),
        };

    // The lookup a replay is answered from, before any re-planning. It must return exactly what was
    // recorded, and it must be scoped to the Inventory the operation was applied to.
    [Fact]
    public async Task A_recorded_operation_can_be_looked_up_by_its_identity_without_re_planning_anything()
    {
        var command = CreateCommand();
        using (var db = CreateContext())
        {
            await new SqlStockMutationStore(db).ApplyAsync(command, CancellationToken.None);
        }

        using var reader = CreateContext();
        var recorded = await new SqlStockMutationStore(reader).FindRecordedAsync(
            new InventoryId(_inventoryId), command.OperationId, CancellationToken.None);

        Assert.NotNull(recorded);
        Assert.True(recorded!.CreatedEntry);
        Assert.Equal("Steel Bolts", recorded.Name);
        Assert.Equal("each", recorded.UnitCanonicalName);
        Assert.Null(recorded.LocationName);
        Assert.Equal("0", recorded.PreviousQuantity.ToInvariantText());
        Assert.Equal("12.5", recorded.ResultingQuantity.ToInvariantText());
    }

    [Fact]
    public async Task An_operation_that_was_never_applied_here_is_simply_not_recorded()
    {
        using var db = CreateContext();

        Assert.Null(await new SqlStockMutationStore(db).FindRecordedAsync(
            new InventoryId(_inventoryId),
            new StockOperationId(Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff")),
            CancellationToken.None));
    }

    // An operation identity means nothing outside the Inventory it was applied to, so looking one up
    // from another Inventory must reveal nothing at all - not the effect, not that it ever happened.
    [Fact]
    public async Task A_recorded_operation_is_invisible_from_another_Inventory()
    {
        var command = CreateCommand();
        using (var db = CreateContext())
        {
            await new SqlStockMutationStore(db).ApplyAsync(command, CancellationToken.None);
        }

        var otherInventoryId = SeedInventory("Other Warehouse", "other warehouse", "seed-2");

        using var reader = CreateContext();
        Assert.Null(await new SqlStockMutationStore(reader).FindRecordedAsync(
            new InventoryId(otherInventoryId), command.OperationId, CancellationToken.None));
    }

    // The window the preflight lookup cannot close: this operation was not in the ledger when it was
    // looked up, but another replica applied that very operation before this save landed. The failing
    // save must converge on re-reporting the twin's recorded effect - never a conflict against itself,
    // and never a second application.
    [Fact]
    public async Task A_create_that_loses_the_race_to_its_own_twin_converges_on_the_recorded_effect()
    {
        var command = CreateCommand();
        using var db = CreateContext();
        ApplyOnceFromACompetingWriterDuring(db, command);

        var result = await new SqlStockMutationStore(db).ApplyAsync(command, CancellationToken.None);

        Assert.Equal(StockMutationStoreOutcome.AlreadyApplied, result.Outcome);
        Assert.True(result.Recorded!.CreatedEntry);
        Assert.Equal("12.5", result.Recorded.ResultingQuantity.ToInvariantText());

        using var reader = CreateContext();
        Assert.Equal(12.5m, Assert.Single(reader.StockEntries.AsNoTracking()).Quantity);
        Assert.Single(reader.StockOperations.AsNoTracking());
        Assert.Single(reader.InventoryAudits.AsNoTracking().Where(a => a.EventType == "StockAdded"));
    }

    [Fact]
    public async Task A_change_that_loses_the_race_to_its_own_twin_converges_on_the_recorded_effect()
    {
        var entryId = SeedStock("Steel Bolts", 10m);
        var command = UpdateCommand(entryId, expected: 10m, resulting: 15m);

        using var db = CreateContext();

        // Loaded before the competitor commits, so this context holds the now-stale concurrency stamp.
        _ = await db.StockEntries.FirstAsync(e => e.Id == entryId);
        ApplyOnceFromACompetingWriterDuring(db, command);

        var result = await new SqlStockMutationStore(db).ApplyAsync(command, CancellationToken.None);

        Assert.Equal(StockMutationStoreOutcome.AlreadyApplied, result.Outcome);
        Assert.Equal("10", result.Recorded!.PreviousQuantity.ToInvariantText());
        Assert.Equal("15", result.Recorded.ResultingQuantity.ToInvariantText());

        using var reader = CreateContext();
        Assert.Equal(15m, reader.StockEntries.AsNoTracking().Single(e => e.Id == entryId).Quantity);
        Assert.Single(reader.StockOperations.AsNoTracking());
        Assert.Single(reader.InventoryAudits.AsNoTracking().Where(a => a.EventType == "StockAdded"));
    }

    /// <summary>
    /// Applies <paramref name="command"/> from a separate connection at the exact moment
    /// <paramref name="db"/> is about to save - after its own ledger lookup has already found nothing.
    /// That is the only window in which a competing replica running the same operation can be missed,
    /// and it is deterministic here rather than a race a test would have to hope for.
    /// </summary>
    private void ApplyOnceFromACompetingWriterDuring(MultiChannelAgentDbContext db, StockMutationCommand command)
    {
        var applied = false;

        db.SavingChanges += (_, _) =>
        {
            if (applied)
            {
                return;
            }

            applied = true;

            using var competitor = CreateContext();
            new SqlStockMutationStore(competitor).ApplyAsync(command, CancellationToken.None).GetAwaiter().GetResult();
        };
    }

    private StockMutationCommand CreateCommand(StockOperationId? operationId = null) => new()    {
        OperationId = operationId ?? new StockOperationId(Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc")),
        InventoryId = new InventoryId(_inventoryId),
        ActorId = _actorId,
        Kind = StockMutationKind.Add,
        Amount = Quantity.Create(12.5m),
        ResultingQuantity = Quantity.Create(12.5m),
        NewEntryName = "Steel Bolts",
        NewEntryUnitId = new UnitId(_unitId),
        NewEntryLocationId = null,
        Note = null,
        NotePreserved = false,
        Now = Now,
    };

    private StockMutationCommand UpdateCommand(Guid entryId, decimal expected, decimal resulting) => new()
    {
        OperationId = new StockOperationId(Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd")),
        InventoryId = new InventoryId(_inventoryId),
        ActorId = _actorId,
        Kind = StockMutationKind.Add,
        Amount = Quantity.Create(resulting - expected),
        ResultingQuantity = Quantity.Create(resulting),
        StockEntryId = new StockEntryId(entryId),
        ExpectedQuantity = Quantity.Create(expected),
        NotePreserved = false,
        Now = Now,
    };

    private Guid SeedStock(string name, decimal quantity)
    {
        using var db = CreateContext();
        var id = Guid.NewGuid();
        db.StockEntries.Add(new StockEntryEntity
        {
            Id = id,
            InventoryId = _inventoryId,
            UnitId = _unitId,
            LocationId = null,
            Name = name,
            NormalizedName = NameNormalization.Normalize(name),
            Quantity = quantity,
            CreatedAt = Now,
        });
        db.SaveChanges();
        return id;
    }

    private void Seed(MultiChannelAgentDbContext db)
    {
        db.Participants.Add(new ParticipantEntity
        {
            Id = _actorId.Value,
            DisplayName = "Editor Person",
            CreatedAt = Now,
            UpdatedAt = Now,
        });
        db.Inventories.Add(new InventoryEntity
        {
            Id = _inventoryId,
            Name = "Warehouse",
            NormalizedName = "warehouse",
            CreatedByParticipantId = _actorId.Value,
            ClientRequestId = "seed-1",
            CreatedAt = Now,
        });
        db.Units.Add(new UnitEntity
        {
            Id = _unitId,
            InventoryId = _inventoryId,
            CanonicalName = "each",
            NormalizedCanonicalName = "each",
            IsReserved = true,
            CreatedAt = Now,
        });
        db.SaveChanges();
    }

    /// <summary>A second Inventory, so a lookup can be attempted from one the operation never touched.</summary>
    private Guid SeedInventory(string name, string normalizedName, string clientRequestId)
    {
        var inventoryId = Guid.NewGuid();

        using var db = CreateContext();
        db.Inventories.Add(new InventoryEntity
        {
            Id = inventoryId,
            Name = name,
            NormalizedName = normalizedName,
            CreatedByParticipantId = _actorId.Value,
            ClientRequestId = clientRequestId,
            CreatedAt = Now,
        });
        db.SaveChanges();

        return inventoryId;
    }

    private MultiChannelAgentDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<MultiChannelAgentDbContext>().UseSqlite(_connectionString).Options);
}
