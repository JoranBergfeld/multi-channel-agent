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

    // Equivalent Stock uniqueness is enforced against the real Location column, with no mirrored
    // sentinel column for any caller to forget to maintain (a caller that got the mirror wrong could
    // insert duplicate Equivalent Stock past the constraint entirely). "Unlocated" is expressed the
    // way the domain expresses it - no Location at all - and covered by its own filtered index,
    // because a relational unique index treats each NULL as distinct.
    [Fact]
    public void StockEntry_enforces_equivalent_stock_uniqueness_against_the_real_location_column()
    {
        using var db = CreateContext();
        var stockEntryType = db.Model.FindEntityType(typeof(StockEntryEntity))!;
        var uniqueIndexes = stockEntryType.GetIndexes().Where(i => i.IsUnique).ToList();

        var locatedIndex = uniqueIndexes.SingleOrDefault(i =>
            i.Properties.Select(p => p.Name).SequenceEqual(new[]
            {
                nameof(StockEntryEntity.InventoryId),
                nameof(StockEntryEntity.NormalizedName),
                nameof(StockEntryEntity.UnitId),
                nameof(StockEntryEntity.LocationId),
            }));
        var unlocatedIndex = uniqueIndexes.SingleOrDefault(i =>
            i.Properties.Select(p => p.Name).SequenceEqual(new[]
            {
                nameof(StockEntryEntity.InventoryId),
                nameof(StockEntryEntity.NormalizedName),
                nameof(StockEntryEntity.UnitId),
            }));

        Assert.NotNull(locatedIndex);
        Assert.NotNull(unlocatedIndex);
        Assert.Contains("IS NOT NULL", locatedIndex!.GetFilter());
        Assert.Contains("IS NULL", unlocatedIndex!.GetFilter());
        Assert.DoesNotContain(
            stockEntryType.GetProperties(),
            property => property.Name.Contains("UniquenessKey", StringComparison.Ordinal));
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
            NormalizedCanonicalName = "each",
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
    private Guid SeedLocation(Guid inventoryId, string name)
    {
        var locationId = Guid.NewGuid();
        using var db = CreateContext();
        db.Locations.Add(new LocationEntity
        {
            Id = locationId,
            InventoryId = inventoryId,
            Name = name,
            NormalizedName = name.ToLowerInvariant(),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
        return locationId;
    }

    private void AddStockEntry(Guid inventoryId, Guid unitId, Guid? locationId, string normalizedName, decimal quantity)
    {
        using var db = CreateContext();
        db.StockEntries.Add(new StockEntryEntity
        {
            Id = Guid.NewGuid(),
            InventoryId = inventoryId,
            UnitId = unitId,
            LocationId = locationId,
            Name = normalizedName,
            NormalizedName = normalizedName,
            Quantity = quantity,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    [Fact]
    public void Two_stock_entries_in_the_same_location_with_the_same_normalized_name_and_unit_violate_the_unique_index()
    {
        var (inventoryId, unitId) = SeedInventoryAndUnit();
        var shelfA = SeedLocation(inventoryId, "Shelf A");
        AddStockEntry(inventoryId, unitId, shelfA, "steel bolts", 5m);

        Assert.Throws<DbUpdateException>(() => AddStockEntry(inventoryId, unitId, shelfA, "steel bolts", 10m));
    }

    // Equivalent Stock includes the Location, so the same name and Unit in two different places - or
    // one placed and one unlocated - are genuinely different Stock Entries.
    [Fact]
    public void The_same_name_and_unit_in_different_locations_are_distinct_stock_entries()
    {
        var (inventoryId, unitId) = SeedInventoryAndUnit();
        var shelfA = SeedLocation(inventoryId, "Shelf A");
        var shelfB = SeedLocation(inventoryId, "Shelf B");

        AddStockEntry(inventoryId, unitId, shelfA, "steel bolts", 5m);
        AddStockEntry(inventoryId, unitId, shelfB, "steel bolts", 7m);
        AddStockEntry(inventoryId, unitId, null, "steel bolts", 9m);

        using var db = CreateContext();
        Assert.Equal(3, db.StockEntries.Count(e => e.InventoryId == inventoryId));
    }

    [Fact]
    public void A_Stock_Entry_carries_a_concurrency_stamp_that_a_writer_must_agree_with()
    {
        using var db = CreateContext();
        var entityType = db.Model.FindEntityType(typeof(StockEntryEntity))!;
        var stamp = entityType.FindProperty(nameof(StockEntryEntity.ConcurrencyStamp))!;

        Assert.True(stamp.IsConcurrencyToken);
    }

    [Fact]
    public void One_operation_identity_can_only_ever_be_recorded_once()
    {
        var (inventoryId, _) = SeedInventoryAndUnit();
        var operationId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

        using (var db = CreateContext())
        {
            db.StockOperations.Add(NewOperation(operationId, inventoryId));
            db.SaveChanges();
        }

        using var second = CreateContext();
        second.StockOperations.Add(NewOperation(operationId, inventoryId));

        Assert.ThrowsAny<DbUpdateException>(() => second.SaveChanges());
    }

    private static StockOperationEntity NewOperation(Guid operationId, Guid inventoryId) => new()
    {
        OperationId = operationId,
        InventoryId = inventoryId,
        Kind = "Add",
        StockEntryId = Guid.NewGuid(),
        Name = "Steel Bolts",
        UnitCanonicalName = "each",
        LocationName = null,
        Note = null,
        PreviousQuantity = 0m,
        ResultingQuantity = 12.5m,
        CreatedEntry = true,
        NotePreserved = false,
        AppliedAt = DateTimeOffset.UtcNow,
    };
}
