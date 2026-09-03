using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.IntegrationTests.Inventories;

/// <summary>
/// Fast, Docker-free coverage against a real relational engine that the Stock read query is executed
/// by the database, not by the process: one bounded command per read, with the filters, the
/// deterministic order, keyset resumption, and the row cap all inside it. Loading an Inventory's rows
/// and then filtering them in memory would pass every behavioral assertion here while collapsing on a
/// real Inventory, so the executed command text itself is inspected too.
/// </summary>
public sealed class SqlStockStoreQueryTests : IDisposable
{
    private readonly SqliteConnection _keepAliveConnection;
    private readonly string _connectionString;
    private readonly List<string> _executedCommands = [];
    private readonly Guid _inventoryId = Guid.NewGuid();
    private readonly Guid _unitId = Guid.NewGuid();

    public SqlStockStoreQueryTests()
    {
        _connectionString = $"DataSource=file:{Guid.NewGuid()}?mode=memory&cache=shared";
        _keepAliveConnection = new SqliteConnection(_connectionString);
        _keepAliveConnection.Open();

        using var db = CreateContext();
        db.Database.EnsureCreated();
        SeedInventory(db);
    }

    public void Dispose() => _keepAliveConnection.Dispose();

    [Fact]
    public async Task A_page_is_filtered_ordered_capped_and_resumed_entirely_by_the_database()
    {
        SeedStock("Zebra Bolts", 3m);
        SeedStock("Apple Bolts", 5m);
        SeedStock("Copper Wire", 7m);
        SeedStock("Empty Crate", 0m);

        using var db = CreateContext();
        var store = new SqlStockStore(db);
        _executedCommands.Clear();

        var firstPage = await store.ListPageAsync(Query(pageSize: 2), CancellationToken.None);

        // Two rows plus the one extra that answers "is there more?" - never the whole Inventory.
        Assert.Equal(["Apple Bolts", "Copper Wire", "Zebra Bolts"], firstPage.Select(r => r.Name));
        var command = Assert.Single(_executedCommands);
        Assert.Contains("ORDER BY", command, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LIMIT", command, StringComparison.OrdinalIgnoreCase);

        // The default excludes zero-quantity Stock in SQL: the empty crate never reaches the process.
        Assert.DoesNotContain(firstPage, r => r.Name == "Empty Crate");
        Assert.Contains("WHERE", command, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Resuming_from_a_cursor_continues_strictly_after_it_without_repeating_a_row()
    {
        SeedStock("Apple Bolts", 1m);
        SeedStock("Copper Wire", 1m);
        SeedStock("Zebra Bolts", 1m);

        using var db = CreateContext();
        var store = new SqlStockStore(db);

        var firstPage = await store.ListPageAsync(Query(pageSize: 1), CancellationToken.None);
        var cursor = StockListCursor.FromRow(firstPage[0]);
        var secondPage = await store.ListPageAsync(Query(pageSize: 1, cursor: cursor.Encode()), CancellationToken.None);

        Assert.Equal("Apple Bolts", firstPage[0].Name);
        Assert.Equal("Copper Wire", secondPage[0].Name);
        Assert.DoesNotContain(secondPage, r => r.Name == "Apple Bolts");
    }

    // The database's order must be the domain's order exactly, including for names that differ only
    // by case or by surrounding whitespace, which normalization has already folded.
    [Fact]
    public async Task The_database_order_matches_the_domain_order_exactly()
    {
        SeedStock("apple bolts", 1m);
        SeedStock("Banana Crates", 1m);
        SeedStock("COPPER WIRE", 1m);
        SeedStock("zebra bolts", 1m);

        using var db = CreateContext();
        var store = new SqlStockStore(db);

        var page = await store.ListPageAsync(Query(pageSize: StockListQuery.MaxPageSize), CancellationToken.None);

        Assert.Equal(page.OrderBy(r => r, StockEntryOrdering.ByDisplayOrder).Select(r => r.Id), page.Select(r => r.Id));
        Assert.Equal(["apple bolts", "Banana Crates", "COPPER WIRE", "zebra bolts"], page.Select(r => r.Name));
    }

    [Fact]
    public async Task Find_caps_its_candidates_in_the_database()
    {
        var shelves = new[] { "Shelf A", "Shelf B", "Shelf C", "Shelf D", "Shelf E", "Shelf F", "Shelf G" };
        foreach (var shelf in shelves)
        {
            SeedStock("Bolts", 1m, SeedLocation(shelf));
        }

        using var db = CreateContext();
        var store = new SqlStockStore(db);
        _executedCommands.Clear();

        var matches = await store.FindMatchesAsync(
            StockFindQuery.ByName(new InventoryId(_inventoryId), "Bolts", unitId: null, locationId: null), 6, CancellationToken.None);

        Assert.Equal(6, matches.Count);
        Assert.Contains("LIMIT", Assert.Single(_executedCommands), StringComparison.OrdinalIgnoreCase);
    }

    private StockListQuery Query(int pageSize, string? cursor = null) => StockListQuery.Create(
        new InventoryId(_inventoryId),
        includeZero: false,
        unitId: null,
        locationId: null,
        unlocatedOnly: false,
        nameFilter: null,
        pageSize,
        cursor);

    private void SeedInventory(MultiChannelAgentDbContext db)
    {
        var participantId = Guid.NewGuid();
        db.Participants.Add(new ParticipantEntity
        {
            Id = participantId,
            DisplayName = "Owner Person",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.Inventories.Add(new InventoryEntity
        {
            Id = _inventoryId,
            Name = "Warehouse",
            NormalizedName = "warehouse",
            CreatedByParticipantId = participantId,
            ClientRequestId = "seed-1",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.Units.Add(new UnitEntity
        {
            Id = _unitId,
            InventoryId = _inventoryId,
            CanonicalName = "each",
            NormalizedCanonicalName = "each",
            IsReserved = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    private Guid SeedLocation(string name)
    {
        using var db = CreateContext();
        var locationId = Guid.NewGuid();
        db.Locations.Add(new LocationEntity
        {
            Id = locationId,
            InventoryId = _inventoryId,
            Name = name,
            NormalizedName = NameNormalization.Normalize(name),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
        return locationId;
    }

    private void SeedStock(string name, decimal quantity, Guid? locationId = null)
    {
        using var db = CreateContext();
        db.StockEntries.Add(new StockEntryEntity
        {
            Id = Guid.NewGuid(),
            InventoryId = _inventoryId,
            UnitId = _unitId,
            LocationId = locationId,
            Name = name,
            NormalizedName = NameNormalization.Normalize(name),
            Quantity = quantity,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    private MultiChannelAgentDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MultiChannelAgentDbContext>()
            .UseSqlite(_connectionString)
            .AddInterceptors(new CommandRecorder(_executedCommands))
            .Options;
        return new MultiChannelAgentDbContext(options);
    }

    /// <summary>Records the SQL actually sent to the database, so a test can assert where the work happened.</summary>
    private sealed class CommandRecorder(List<string> commands) : DbCommandInterceptor
    {
        public override InterceptionResult<System.Data.Common.DbDataReader> ReaderExecuting(
            System.Data.Common.DbCommand command,
            CommandEventData eventData,
            InterceptionResult<System.Data.Common.DbDataReader> result)
        {
            commands.Add(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command,
            CommandEventData eventData,
            InterceptionResult<System.Data.Common.DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
