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
/// Fast, Docker-free coverage against a real relational engine that
/// <see cref="SqlInventoryReferenceStore.ResolveUnitsAsync"/> and
/// <see cref="SqlInventoryReferenceStore.ResolveLocationsAsync"/> each translate to exactly one SQL
/// command, however many distinct terms are asked for - proving the fix for the root cause a
/// per-distinct-term implementation would still have: one round trip per distinct term, up to 5,000
/// for a valid file, even with caching. An empty request must never even reach the database, and
/// active-only, no-creation, and the shared Unit term namespace must all still hold in this batched
/// shape exactly as they do for the single-term path.
/// </summary>
public sealed class SqlInventoryReferenceStoreBatchTests : IDisposable
{
    private readonly SqliteConnection _keepAliveConnection;
    private readonly string _connectionString;
    private readonly List<string> _executedCommands = [];
    private readonly Guid _inventoryId = Guid.NewGuid();
    private Guid _eachId;

    public SqlInventoryReferenceStoreBatchTests()
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
    public async Task Many_distinct_Unit_terms_resolve_in_exactly_one_database_command()
    {
        var boxId = SeedUnit("Cardboard Box", "boxes", "bx");
        var drumId = SeedUnit("Steel Drum");

        using var db = CreateContext();
        var store = new SqlInventoryReferenceStore(db);
        _executedCommands.Clear();

        var result = await store.ResolveUnitsAsync(
            new InventoryId(_inventoryId),
            ["each", "bx", "boxes", "steel drum", "unknown-unit"],
            CancellationToken.None);

        Assert.Single(_executedCommands);
        Assert.Equal(4, result.Count);
        Assert.Equal(new ResolvedUnitReference(new UnitId(_eachId), "each"), result["each"]);
        Assert.Equal(new ResolvedUnitReference(new UnitId(boxId), "Cardboard Box"), result["bx"]);
        Assert.Equal(new ResolvedUnitReference(new UnitId(boxId), "Cardboard Box"), result["boxes"]);
        Assert.Equal(new ResolvedUnitReference(new UnitId(drumId), "Steel Drum"), result["steel drum"]);
        Assert.False(result.ContainsKey("unknown-unit"));
    }

    [Fact]
    public async Task A_retired_Unit_and_a_retired_term_are_both_absent_from_a_batch_result()
    {
        var activeId = SeedUnit("Steel Drum");
        var retiredId = SeedUnit("Cardboard Box", "boxes");
        RetireUnit(retiredId);

        var retiredAliasOwnerId = SeedUnit("Plastic Tote", "tote-old");
        RetireUnitTerm(retiredAliasOwnerId, "tote-old");

        using var db = CreateContext();
        var store = new SqlInventoryReferenceStore(db);

        var result = await store.ResolveUnitsAsync(
            new InventoryId(_inventoryId),
            ["steel drum", "cardboard box", "boxes", "plastic tote", "tote-old"],
            CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal(new ResolvedUnitReference(new UnitId(activeId), "Steel Drum"), result["steel drum"]);
        Assert.Equal("Plastic Tote", result["plastic tote"].CanonicalName);
        Assert.False(result.ContainsKey("cardboard box"));
        Assert.False(result.ContainsKey("boxes"));
        Assert.False(result.ContainsKey("tote-old"));
    }

    [Fact]
    public async Task An_empty_Unit_term_request_never_reaches_the_database()
    {
        using var db = CreateContext();
        var store = new SqlInventoryReferenceStore(db);
        _executedCommands.Clear();

        var result = await store.ResolveUnitsAsync(new InventoryId(_inventoryId), [], CancellationToken.None);

        Assert.Empty(result);
        Assert.Empty(_executedCommands);
    }

    [Fact]
    public async Task Many_distinct_Location_names_resolve_in_exactly_one_database_command()
    {
        var shelfId = SeedLocation("Shelf A");
        var bayId = SeedLocation("Bay 9");

        using var db = CreateContext();
        var store = new SqlInventoryReferenceStore(db);
        _executedCommands.Clear();

        var result = await store.ResolveLocationsAsync(
            new InventoryId(_inventoryId), ["shelf a", "bay 9", "unknown-location"], CancellationToken.None);

        Assert.Single(_executedCommands);
        Assert.Contains("json_each", _executedCommands[0]);
        Assert.Equal(2, result.Count);
        Assert.Equal(new ResolvedLocationReference(new LocationId(shelfId), "Shelf A"), result["shelf a"]);
        Assert.Equal(new ResolvedLocationReference(new LocationId(bayId), "Bay 9"), result["bay 9"]);
        Assert.False(result.ContainsKey("unknown-location"));
    }

    [Fact]
    public async Task A_retired_Location_is_absent_from_a_batch_result()
    {
        var activeId = SeedLocation("Shelf A");
        var retiredId = SeedLocation("Bay 9");
        RetireLocation(retiredId);

        using var db = CreateContext();
        var store = new SqlInventoryReferenceStore(db);

        var result = await store.ResolveLocationsAsync(
            new InventoryId(_inventoryId), ["shelf a", "bay 9"], CancellationToken.None);

        Assert.Single(result);
        Assert.Equal(new ResolvedLocationReference(new LocationId(activeId), "Shelf A"), result["shelf a"]);
    }

    [Fact]
    public async Task An_empty_Location_name_request_never_reaches_the_database()
    {
        using var db = CreateContext();
        var store = new SqlInventoryReferenceStore(db);
        _executedCommands.Clear();

        var result = await store.ResolveLocationsAsync(new InventoryId(_inventoryId), [], CancellationToken.None);

        Assert.Empty(result);
        Assert.Empty(_executedCommands);
    }

    [Fact]
    public async Task Five_thousand_distinct_Unit_terms_still_resolve_in_exactly_one_database_command()
    {
        var terms = new List<string>(5_000);
        for (var index = 0; index < 5_000; index++)
        {
            SeedUnit($"Unit {index}");
            terms.Add($"unit {index}");
        }

        using var db = CreateContext();
        var store = new SqlInventoryReferenceStore(db);
        _executedCommands.Clear();

        var result = await store.ResolveUnitsAsync(new InventoryId(_inventoryId), terms, CancellationToken.None);

        Assert.Single(_executedCommands);
        Assert.Equal(5_000, result.Count);

        // The translation itself, not just the round-trip count: a single array-like parameter
        // unnested by the database (json_each on SQLite, OPENJSON on SQL Server), never one parameter
        // per term - which is what makes this safe against SQL Server's parameter ceiling at 5,000.
        Assert.Contains("json_each", _executedCommands[0]);
    }

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
        _eachId = Guid.NewGuid();
        db.Units.Add(new UnitEntity
        {
            Id = _eachId,
            InventoryId = _inventoryId,
            CanonicalName = "each",
            NormalizedCanonicalName = "each",
            IsReserved = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.UnitTerms.Add(new UnitTermEntity
        {
            Id = Guid.NewGuid(),
            InventoryId = _inventoryId,
            UnitId = _eachId,
            Term = "each",
            NormalizedTerm = "each",
            IsCanonical = true,
            IsReserved = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    private Guid SeedUnit(string canonicalName, params string[] aliases)
    {
        using var db = CreateContext();
        var unitId = Guid.NewGuid();
        db.Units.Add(new UnitEntity
        {
            Id = unitId,
            InventoryId = _inventoryId,
            CanonicalName = canonicalName,
            NormalizedCanonicalName = NameNormalization.Normalize(canonicalName),
            IsReserved = false,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.UnitTerms.Add(new UnitTermEntity
        {
            Id = Guid.NewGuid(),
            InventoryId = _inventoryId,
            UnitId = unitId,
            Term = canonicalName,
            NormalizedTerm = NameNormalization.Normalize(canonicalName),
            IsCanonical = true,
            IsReserved = false,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        foreach (var alias in aliases)
        {
            db.UnitTerms.Add(new UnitTermEntity
            {
                Id = Guid.NewGuid(),
                InventoryId = _inventoryId,
                UnitId = unitId,
                Term = alias,
                NormalizedTerm = NameNormalization.Normalize(alias),
                IsCanonical = false,
                IsReserved = false,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        db.SaveChanges();
        return unitId;
    }

    private void RetireUnit(Guid unitId)
    {
        using var db = CreateContext();
        var now = DateTimeOffset.UtcNow;
        db.Units.Where(u => u.Id == unitId).ExecuteUpdate(setters => setters.SetProperty(u => u.RetiredAt, now));
        db.UnitTerms.Where(t => t.UnitId == unitId).ExecuteUpdate(setters => setters.SetProperty(t => t.RetiredAt, now));
    }

    private void RetireUnitTerm(Guid unitId, string term)
    {
        using var db = CreateContext();
        var normalized = NameNormalization.Normalize(term);
        db.UnitTerms
            .Where(t => t.UnitId == unitId && t.NormalizedTerm == normalized)
            .ExecuteUpdate(setters => setters.SetProperty(t => t.RetiredAt, DateTimeOffset.UtcNow));
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

    private void RetireLocation(Guid locationId)
    {
        using var db = CreateContext();
        db.Locations
            .Where(l => l.Id == locationId)
            .ExecuteUpdate(setters => setters.SetProperty(l => l.RetiredAt, DateTimeOffset.UtcNow));
    }

    private MultiChannelAgentDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MultiChannelAgentDbContext>()
            .UseSqlite(_connectionString)
            .AddInterceptors(new CommandRecorder(_executedCommands))
            .Options;
        return new MultiChannelAgentDbContext(options);
    }

    /// <summary>Records the SQL actually sent to the database, so a test can assert one command answers a whole batch.</summary>
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
