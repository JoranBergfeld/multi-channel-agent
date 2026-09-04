using MultiChannelAgent.Application.Inventories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.IntegrationTests.Inventories;

/// <summary>
/// The SQL-backed catalog reads Unit and Location administration rests on: active-only listing in
/// the deterministic display order, keyset paging, a Unit's own terms with their reserved state,
/// current versions, how many Stock Entries reference something, and bounded deterministic
/// suggestions.
/// </summary>
public sealed class SqlReferenceCatalogStoreTests : SqlIntegrationTestBase
{
    private MultiChannelAgentDbContext Db(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();

    private async Task<(Guid InventoryId, Guid EachUnitId)> SeedInventoryAsync()
    {
        using var scope = Factory!.Services.CreateScope();
        var db = Db(scope);

        var inventoryId = Guid.NewGuid();
        var participantId = Guid.NewGuid();

        db.Participants.Add(new ParticipantEntity
        {
            Id = participantId,
            DisplayName = "Catalog Owner",
            CreatedAt = DateTimeOffset.UnixEpoch,
        });
        db.Inventories.Add(new InventoryEntity
        {
            Id = inventoryId,
            Name = "Catalog Warehouse",
            NormalizedName = "catalog warehouse",
            CreatedByParticipantId = participantId,
            ClientRequestId = Guid.NewGuid().ToString(),
            CreatedAt = DateTimeOffset.UnixEpoch,
        });
        db.Memberships.Add(new MembershipEntity
        {
            InventoryId = inventoryId,
            ParticipantId = participantId,
            Role = MembershipRole.Owner,
            CreatedAt = DateTimeOffset.UnixEpoch,
        });

        var each = Unit.CreateReservedEach(new InventoryId(inventoryId), DateTimeOffset.UnixEpoch);
        db.Units.Add(new UnitEntity
        {
            Id = each.Id.Value,
            InventoryId = inventoryId,
            CanonicalName = each.CanonicalName,
            NormalizedCanonicalName = NameNormalization.Normalize(each.CanonicalName),
            IsReserved = true,
            ConcurrencyStamp = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UnixEpoch,
        });

        foreach (var term in each.Terms())
        {
            db.UnitTerms.Add(new UnitTermEntity
            {
                Id = Guid.NewGuid(),
                InventoryId = inventoryId,
                UnitId = each.Id.Value,
                Term = term.Term,
                NormalizedTerm = term.NormalizedTerm,
                IsCanonical = term.IsCanonical,
                IsReserved = true,
                CreatedAt = DateTimeOffset.UnixEpoch,
            });
        }

        await db.SaveChangesAsync();

        return (inventoryId, each.Id.Value);
    }

    private async Task<Guid> SeedUnitAsync(Guid inventoryId, string canonicalName, string[] aliases, bool retired = false)
    {
        using var scope = Factory!.Services.CreateScope();
        var db = Db(scope);

        var unit = Unit.Create(new InventoryId(inventoryId), canonicalName, aliases, DateTimeOffset.UnixEpoch);
        var retiredAt = retired ? (DateTimeOffset?)DateTimeOffset.UnixEpoch.AddDays(1) : null;

        db.Units.Add(new UnitEntity
        {
            Id = unit.Id.Value,
            InventoryId = inventoryId,
            CanonicalName = unit.CanonicalName,
            NormalizedCanonicalName = NameNormalization.Normalize(unit.CanonicalName),
            IsReserved = false,
            ConcurrencyStamp = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UnixEpoch,
            RetiredAt = retiredAt,
        });

        foreach (var term in unit.Terms())
        {
            db.UnitTerms.Add(new UnitTermEntity
            {
                Id = Guid.NewGuid(),
                InventoryId = inventoryId,
                UnitId = unit.Id.Value,
                Term = term.Term,
                NormalizedTerm = term.NormalizedTerm,
                IsCanonical = term.IsCanonical,
                IsReserved = false,
                CreatedAt = DateTimeOffset.UnixEpoch,
                RetiredAt = retiredAt,
            });
        }

        await db.SaveChangesAsync();

        return unit.Id.Value;
    }

    private async Task<Guid> SeedLocationAsync(Guid inventoryId, string name, bool retired = false)
    {
        using var scope = Factory!.Services.CreateScope();
        var db = Db(scope);

        var location = Location.Create(new InventoryId(inventoryId), name, DateTimeOffset.UnixEpoch);

        db.Locations.Add(new LocationEntity
        {
            Id = location.Id.Value,
            InventoryId = inventoryId,
            Name = location.Name,
            NormalizedName = location.NormalizedName,
            ConcurrencyStamp = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UnixEpoch,
            RetiredAt = retired ? DateTimeOffset.UnixEpoch.AddDays(1) : null,
        });

        await db.SaveChangesAsync();

        return location.Id.Value;
    }

    private async Task SeedStockAsync(Guid inventoryId, Guid unitId, Guid? locationId, string name)
    {
        using var scope = Factory!.Services.CreateScope();
        var db = Db(scope);

        db.StockEntries.Add(new StockEntryEntity
        {
            Id = Guid.NewGuid(),
            InventoryId = inventoryId,
            UnitId = unitId,
            LocationId = locationId,
            Name = name,
            NormalizedName = NameNormalization.Normalize(name),
            Quantity = 1m,
            CreatedAt = DateTimeOffset.UnixEpoch,
        });

        await db.SaveChangesAsync();
    }

    [SkippableFact]
    public async Task Listing_Units_returns_active_ones_in_display_order_with_their_active_aliases()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed catalog read.");

        var (inventoryId, _) = await SeedInventoryAsync();
        await SeedUnitAsync(inventoryId, "Cardboard Box", ["boxes", "bx"]);
        await SeedUnitAsync(inventoryId, "Pallet", [], retired: true);

        using var scope = Factory!.Services.CreateScope();
        var store = new SqlReferenceCatalogStore(Db(scope));

        var page = await store.ListUnitsAsync(
            ReferenceListQuery.Create(new InventoryId(inventoryId), ReferenceKind.Unit, pageSize: null, cursor: null),
            CancellationToken.None);

        Assert.Equal(["Cardboard Box", "each"], page.Select(row => row.CanonicalName));
        Assert.Equal(["boxes", "bx"], page[0].Aliases);
        Assert.Equal(["pc", "pcs", "piece", "pieces"], page[1].Aliases.OrderBy(alias => alias, StringComparer.Ordinal));
    }

    [SkippableFact]
    public async Task Listing_Units_pages_by_keyset_without_repeating_or_skipping_a_row()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed catalog read.");

        var (inventoryId, _) = await SeedInventoryAsync();
        await SeedUnitAsync(inventoryId, "Alpha", []);
        await SeedUnitAsync(inventoryId, "Bravo", []);

        using var scope = Factory!.Services.CreateScope();
        var store = new SqlReferenceCatalogStore(Db(scope));

        var first = await store.ListUnitsAsync(
            ReferenceListQuery.Create(new InventoryId(inventoryId), ReferenceKind.Unit, pageSize: 2, cursor: null),
            CancellationToken.None);

        Assert.Equal(["Alpha", "Bravo"], first.Take(2).Select(row => row.CanonicalName));

        var cursor = new ReferenceListCursor(
            ReferenceKind.Unit, new ReferenceOrderKey(first[1].NormalizedCanonicalName, first[1].Id.Value.ToString("D"))).Encode();

        var second = await store.ListUnitsAsync(
            ReferenceListQuery.Create(new InventoryId(inventoryId), ReferenceKind.Unit, pageSize: 2, cursor),
            CancellationToken.None);

        Assert.Equal(["each"], second.Select(row => row.CanonicalName));
    }

    [SkippableFact]
    public async Task Listing_Locations_returns_active_ones_in_display_order()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed catalog read.");

        var (inventoryId, _) = await SeedInventoryAsync();
        await SeedLocationAsync(inventoryId, "Shelf B");
        await SeedLocationAsync(inventoryId, "Shelf A");
        await SeedLocationAsync(inventoryId, "Old Bay", retired: true);

        using var scope = Factory!.Services.CreateScope();
        var store = new SqlReferenceCatalogStore(Db(scope));

        var page = await store.ListLocationsAsync(
            ReferenceListQuery.Create(new InventoryId(inventoryId), ReferenceKind.Location, pageSize: null, cursor: null),
            CancellationToken.None);

        Assert.Equal(["Shelf A", "Shelf B"], page.Select(row => row.Name));
    }

    [SkippableFact]
    public async Task Finding_a_Unit_for_administration_reports_its_terms_its_reserved_state_and_its_version()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed catalog read.");

        var (inventoryId, eachUnitId) = await SeedInventoryAsync();
        var boxId = await SeedUnitAsync(inventoryId, "Cardboard Box", ["boxes"]);

        using var scope = Factory!.Services.CreateScope();
        var store = new SqlReferenceCatalogStore(Db(scope));

        var box = await store.FindUnitAsync(new InventoryId(inventoryId), new UnitId(boxId), CancellationToken.None);
        var each = await store.FindUnitAsync(new InventoryId(inventoryId), new UnitId(eachUnitId), CancellationToken.None);

        Assert.NotNull(box);
        Assert.False(box!.IsReserved);
        Assert.NotEqual(Guid.Empty, box.ConcurrencyStamp);
        Assert.Equal(["Cardboard Box", "boxes"], box.Terms.Select(term => term.Term));
        Assert.True(box.Terms[0].IsCanonical);

        Assert.NotNull(each);
        Assert.True(each!.IsReserved);
        Assert.All(each.Terms, term => Assert.True(term.IsReserved));
    }

    [SkippableFact]
    public async Task A_retired_reference_is_not_found_for_administration_either()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed catalog read.");

        var (inventoryId, _) = await SeedInventoryAsync();
        var palletId = await SeedUnitAsync(inventoryId, "Pallet", [], retired: true);
        var bayId = await SeedLocationAsync(inventoryId, "Old Bay", retired: true);

        using var scope = Factory!.Services.CreateScope();
        var store = new SqlReferenceCatalogStore(Db(scope));

        Assert.Null(await store.FindUnitAsync(new InventoryId(inventoryId), new UnitId(palletId), CancellationToken.None));
        Assert.Null(await store.FindLocationAsync(new InventoryId(inventoryId), new LocationId(bayId), CancellationToken.None));
    }

    [SkippableFact]
    public async Task The_active_term_namespace_excludes_retired_terms_and_can_exclude_one_Units_own()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed catalog read.");

        var (inventoryId, _) = await SeedInventoryAsync();
        var boxId = await SeedUnitAsync(inventoryId, "Cardboard Box", ["boxes"]);
        await SeedUnitAsync(inventoryId, "Pallet", ["pallets"], retired: true);

        using var scope = Factory!.Services.CreateScope();
        var store = new SqlReferenceCatalogStore(Db(scope));

        var all = await store.ReadActiveUnitTermsAsync(new InventoryId(inventoryId), excluding: null, CancellationToken.None);
        var others = await store.ReadActiveUnitTermsAsync(new InventoryId(inventoryId), new UnitId(boxId), CancellationToken.None);

        Assert.Contains("cardboard box", all);
        Assert.Contains("each", all);
        Assert.DoesNotContain("pallet", all);
        Assert.DoesNotContain("pallets", all);

        Assert.DoesNotContain("cardboard box", others);
        Assert.Contains("boxes", others);
        Assert.Contains("each", others);
    }

    [SkippableFact]
    public async Task Counting_Stock_references_answers_exactly_what_blocks_a_Retire()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed catalog read.");

        var (inventoryId, eachUnitId) = await SeedInventoryAsync();
        var boxId = await SeedUnitAsync(inventoryId, "Cardboard Box", []);
        var shelfId = await SeedLocationAsync(inventoryId, "Shelf A");
        await SeedStockAsync(inventoryId, eachUnitId, shelfId, "Steel Bolts");

        using var scope = Factory!.Services.CreateScope();
        var store = new SqlReferenceCatalogStore(Db(scope));

        Assert.Equal(1, await store.CountStockReferencesAsync(
            new InventoryId(inventoryId), ReferenceKind.Unit, eachUnitId, CancellationToken.None));
        Assert.Equal(0, await store.CountStockReferencesAsync(
            new InventoryId(inventoryId), ReferenceKind.Unit, boxId, CancellationToken.None));
        Assert.Equal(1, await store.CountStockReferencesAsync(
            new InventoryId(inventoryId), ReferenceKind.Location, shelfId, CancellationToken.None));
    }

    [SkippableFact]
    public async Task Suggestions_are_bounded_deterministic_and_never_fuzzy()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed catalog read.");

        var (inventoryId, _) = await SeedInventoryAsync();
        await SeedUnitAsync(inventoryId, "Box Large", []);
        await SeedUnitAsync(inventoryId, "Box Small", []);
        await SeedUnitAsync(inventoryId, "Crate", []);

        using var scope = Factory!.Services.CreateScope();
        var store = new SqlReferenceCatalogStore(Db(scope));

        var prefixed = await store.SuggestAsync(new InventoryId(inventoryId), ReferenceKind.Unit, "box", CancellationToken.None);
        Assert.Equal(["Box Large", "Box Small"], prefixed);

        // "bx" shares no prefix with anything, so the answer falls back to what actually exists,
        // still bounded and still in the one deterministic order - never a nearest-match guess.
        var fallback = await store.SuggestAsync(new InventoryId(inventoryId), ReferenceKind.Unit, "zzz", CancellationToken.None);
        Assert.Equal(IReferenceCatalogStore.MaxSuggestions, fallback.Count);
        Assert.Equal("Box Large", fallback[0]);
    }
}
