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

    private StockMutationCommand CreateCommand(StockOperationId? operationId = null) => new()
    {
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

    private MultiChannelAgentDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<MultiChannelAgentDbContext>().UseSqlite(_connectionString).Options);
}
