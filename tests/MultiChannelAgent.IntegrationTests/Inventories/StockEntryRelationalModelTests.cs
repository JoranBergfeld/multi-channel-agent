using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.IntegrationTests.Inventories;

/// <summary>
/// Fast, Docker-free relational coverage for the Stock Entry model: the compiled EF Core model's
/// Equivalent Stock unique index and Quantity precision/scale (inspected directly, no database
/// connection needed), plus real relational-engine behavior (SQLite, mirroring the pattern already
/// proven at <see cref="SqlInboxStoreConcurrencyTests"/>) for the unique index actually rejecting a
/// duplicate Equivalent Stock row and an exact decimal Quantity round-tripping unrounded. The real
/// SQL Server Testcontainers proof that a fresh database migrates this model cleanly lives in the
/// SQL-backed conversational scenario.
/// </summary>
public sealed class StockEntryRelationalModelTests : IDisposable
{
    private readonly SqliteConnection _keepAliveConnection;
    private readonly string _connectionString;

    public StockEntryRelationalModelTests()
    {
        _connectionString = $"DataSource=file:{Guid.NewGuid()}?mode=memory&cache=shared";
        _keepAliveConnection = new SqliteConnection(_connectionString);
        _keepAliveConnection.Open();

        using var db = CreateContext();
        db.Database.EnsureCreated();
    }

    private MultiChannelAgentDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MultiChannelAgentDbContext>()
            .UseSqlite(_connectionString)
            .Options;
        return new MultiChannelAgentDbContext(options);
    }

    public void Dispose() => _keepAliveConnection.Dispose();

    [Fact]
    public void StockEntry_still_enforces_the_unique_equivalent_stock_index()
    {
        using var db = CreateContext();
        var model = db.Model;
        var stockEntryType = model.FindEntityType(typeof(StockEntryEntity))!;

        var uniqueIndex = stockEntryType.GetIndexes().SingleOrDefault(i =>
            i.IsUnique &&
            i.Properties.Select(p => p.Name).SequenceEqual(new[]
            {
                nameof(StockEntryEntity.InventoryId),
                nameof(StockEntryEntity.NormalizedName),
                nameof(StockEntryEntity.UnitId),
                nameof(StockEntryEntity.LocationUniquenessKey),
            }));

        Assert.NotNull(uniqueIndex);
    }

    [Fact]
    public void StockEntry_Quantity_has_a_generous_fixed_precision_and_scale()
    {
        using var db = CreateContext();
        var stockEntryType = db.Model.FindEntityType(typeof(StockEntryEntity))!;
        var quantityProperty = stockEntryType.FindProperty(nameof(StockEntryEntity.Quantity))!;

        Assert.Equal(28, quantityProperty.GetPrecision());
        Assert.Equal(10, quantityProperty.GetScale());
    }

    private (Guid InventoryId, Guid UnitId) SeedInventoryAndUnit()
    {
        var inventoryId = Guid.NewGuid();
        var unitId = Guid.NewGuid();
        var participantId = Guid.NewGuid();

        using var db = CreateContext();
        db.Participants.Add(new ParticipantEntity
        {
            Id = participantId,
            DisplayName = "Owner Person",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.Inventories.Add(new InventoryEntity
        {
            Id = inventoryId,
            Name = "Warehouse",
            NormalizedName = "warehouse",
            CreatedByParticipantId = participantId,
            ClientRequestId = "seed-1",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.Units.Add(new UnitEntity
        {
            Id = unitId,
            InventoryId = inventoryId,
            CanonicalName = "each",
            IsReserved = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();

        return (inventoryId, unitId);
    }

    [Fact]
    public void Two_unlocated_stock_entries_with_the_same_normalized_name_and_unit_violate_the_unique_index()
    {
        var (inventoryId, unitId) = SeedInventoryAndUnit();

        using (var db = CreateContext())
        {
            db.StockEntries.Add(new StockEntryEntity
            {
                Id = Guid.NewGuid(),
                InventoryId = inventoryId,
                UnitId = unitId,
                LocationId = null,
                LocationUniquenessKey = Guid.Empty,
                Name = "Steel Bolts",
                NormalizedName = "steel bolts",
                Quantity = 5m,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.SaveChanges();
        }

        using var conflictingDb = CreateContext();
        conflictingDb.StockEntries.Add(new StockEntryEntity
        {
            Id = Guid.NewGuid(),
            InventoryId = inventoryId,
            UnitId = unitId,
            LocationId = null,
            LocationUniquenessKey = Guid.Empty,
            Name = "steel   bolts",
            NormalizedName = "steel bolts",
            Quantity = 10m,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        Assert.Throws<DbUpdateException>(() => conflictingDb.SaveChanges());
    }

    [Fact]
    public async Task An_exact_decimal_quantity_round_trips_without_rounding()
    {
        var (inventoryId, unitId) = SeedInventoryAndUnit();
        var stockEntryId = Guid.NewGuid();
        const decimal exactQuantity = 12.3456789012m;

        using (var db = CreateContext())
        {
            db.StockEntries.Add(new StockEntryEntity
            {
                Id = stockEntryId,
                InventoryId = inventoryId,
                UnitId = unitId,
                LocationId = null,
                LocationUniquenessKey = Guid.Empty,
                Name = "Steel Bolts",
                NormalizedName = "steel bolts",
                Quantity = exactQuantity,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        using var readDb = CreateContext();
        var reloaded = await readDb.StockEntries.AsNoTracking().SingleAsync(e => e.Id == stockEntryId);

        Assert.Equal(exactQuantity, reloaded.Quantity);
    }
}
