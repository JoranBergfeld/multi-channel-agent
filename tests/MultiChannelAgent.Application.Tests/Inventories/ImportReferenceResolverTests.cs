using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Tests.TestDoubles.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.Inventories;

public class ImportReferenceResolverTests
{
    private readonly InventoryId _inventoryId = new(Guid.NewGuid());
    private readonly UnitId _eachId = new(Guid.NewGuid());
    private readonly UnitId _boxId = new(Guid.NewGuid());
    private readonly LocationId _shelfId = new(Guid.NewGuid());
    private readonly InMemoryInventoryReferenceStore _references = new();
    private readonly InMemoryReferenceCatalogStore _catalog = new();

    public ImportReferenceResolverTests()
    {
        _references.AddUnit(_inventoryId, _eachId, "each", "piece", "pieces", "pc", "pcs");
        _references.AddUnit(_inventoryId, _boxId, "Cardboard Box", "boxes", "bx");
        _references.AddLocation(_inventoryId, _shelfId, "Shelf A");
    }

    private ImportReferenceResolver Resolver() => new(_references, _catalog);

    private static ImportRow Row(
        int lineNumber = 2,
        string name = "Steel Bolts",
        string unitTerm = "each",
        string? locationName = null,
        string? note = null) => new()
        {
            LineNumber = lineNumber,
            Name = name,
            NormalizedName = NameNormalization.Normalize(name),
            Quantity = Quantity.Create(4m),
            UnitTerm = unitTerm,
            LocationName = locationName,
            Note = note,
        };

    private Task<ImportResolutionResult> ResolveAsync(params ImportRow[] rows) =>
        Resolver().ResolveAsync(_inventoryId, rows, CancellationToken.None);

    [Fact]
    public async Task A_Unit_resolves_by_its_canonical_name_or_by_any_active_alias()
    {
        var result = await ResolveAsync(Row(2, unitTerm: "Cardboard Box"), Row(3, unitTerm: "bx"), Row(4, unitTerm: "BOXES"));

        Assert.Empty(result.Errors);
        Assert.All(result.Rows, row => Assert.Equal(_boxId, row.UnitId));

        // The Inventory's own canonical name is what the preview shows, not what the file typed.
        Assert.All(result.Rows, row => Assert.Equal("Cardboard Box", row.UnitCanonicalName));
    }

    [Fact]
    public async Task A_Location_resolves_by_its_exact_active_name_however_it_is_cased()
    {
        var result = await ResolveAsync(Row(locationName: "  shelf   a "));

        Assert.Empty(result.Errors);
        Assert.Equal(_shelfId, Assert.Single(result.Rows).LocationId);
        Assert.Equal("Shelf A", result.Rows[0].LocationName);
    }

    [Fact]
    public async Task An_absent_Location_stays_absent_because_unlocated_is_not_a_reference()
    {
        var result = await ResolveAsync(Row(locationName: null));

        Assert.Null(Assert.Single(result.Rows).LocationId);
        Assert.Null(result.Rows[0].LocationName);
    }

    [Fact]
    public async Task An_unknown_Unit_is_reported_at_its_own_column_and_never_created()
    {
        var result = await ResolveAsync(Row(unitTerm: "crate"));

        Assert.Empty(result.Rows);
        var error = Assert.Single(result.Errors);
        Assert.Equal(ImportErrorCode.UnknownUnit, error.Code);
        Assert.Equal(2, error.LineNumber);
        Assert.Equal(ImportContract.UnitColumn, error.ColumnIndex);
    }

    [Fact]
    public async Task An_unknown_Location_is_reported_at_its_own_column()
    {
        var result = await ResolveAsync(Row(locationName: "Bay 9"));

        var error = Assert.Single(result.Errors);
        Assert.Equal(ImportErrorCode.UnknownLocation, error.Code);
        Assert.Equal(ImportContract.LocationColumn, error.ColumnIndex);
    }

    [Fact]
    public async Task A_retired_reference_is_exactly_as_unknown_as_one_that_never_existed()
    {
        _references.RetireUnit(_inventoryId, _boxId);

        var result = await ResolveAsync(Row(unitTerm: "Cardboard Box"));

        Assert.Equal(ImportErrorCode.UnknownUnit, Assert.Single(result.Errors).Code);
    }

    [Fact]
    public async Task An_unknown_reference_carries_bounded_deterministic_suggestions()
    {
        _catalog.AddUnit(_inventoryId, "Crate Large", []);
        _catalog.AddUnit(_inventoryId, "Crate Small", []);

        var result = await ResolveAsync(Row(unitTerm: "crate"));

        var error = Assert.Single(result.Errors);
        Assert.Equal(["Crate Large", "Crate Small"], error.Suggestions);
        Assert.True(error.Suggestions.Count <= IReferenceCatalogStore.MaxSuggestions);
    }

    [Fact]
    public async Task Every_unresolvable_row_is_reported_so_one_pass_fixes_the_file()
    {
        var result = await ResolveAsync(Row(2, unitTerm: "crate"), Row(3, locationName: "Bay 9"), Row(4, unitTerm: "drum"));

        Assert.Equal(3, result.Errors.Count);
        Assert.Equal([2, 3, 4], result.Errors.Select(error => error.LineNumber));
    }

    [Fact]
    public async Task A_row_with_both_an_unknown_Unit_and_an_unknown_Location_reports_both_so_one_pass_fixes_it()
    {
        var result = await ResolveAsync(Row(unitTerm: "crate", locationName: "Bay 9"));

        Assert.Empty(result.Rows);
        Assert.Equal(2, result.Errors.Count);
        Assert.Equal(
            [ImportErrorCode.UnknownUnit, ImportErrorCode.UnknownLocation],
            result.Errors.Select(error => error.Code));
        Assert.All(result.Errors, error => Assert.Equal(2, error.LineNumber));
        Assert.Equal(ImportContract.UnitColumn, result.Errors[0].ColumnIndex);
        Assert.Equal(ImportContract.LocationColumn, result.Errors[1].ColumnIndex);
    }

    [Fact]
    public async Task One_distinct_term_is_looked_up_once_however_many_rows_use_it()
    {
        // Case/whitespace variants of the same term, so a refactor that keys the cache by raw text
        // instead of the normalized form cannot pass by accident.
        var variants = new[] { "bx", " bx ", "BX", "bx  " };
        var rows = Enumerable.Range(0, 50).Select(index => Row(index + 2, unitTerm: variants[index % variants.Length])).ToArray();

        var result = await ResolveAsync(rows);

        Assert.Empty(result.Errors);
        Assert.Equal(50, result.Rows.Count);
        Assert.Equal(1, _references.UnitResolutionCount);

        // Identity resolution being cached is not enough on its own: a refactor could still cache only
        // the UnitId and make one display-name round trip per row. This proves both are bounded.
        Assert.Equal(1, _references.UnitCanonicalNameLookupCount);
    }

    [Fact]
    public async Task One_distinct_Location_name_is_looked_up_once_however_many_rows_use_it()
    {
        var variants = new[] { "Shelf A", "  shelf a", "SHELF   A", "shelf a " };
        var rows = Enumerable.Range(0, 50)
            .Select(index => Row(index + 2, locationName: variants[index % variants.Length]))
            .ToArray();

        var result = await ResolveAsync(rows);

        Assert.Empty(result.Errors);
        Assert.Equal(50, result.Rows.Count);
        Assert.Equal(1, _references.LocationResolutionCount);
        Assert.Equal(1, _references.LocationNameLookupCount);
    }

    [Fact]
    public async Task An_unknown_term_is_looked_up_once_however_many_rows_share_it_because_a_negative_result_is_cached_too()
    {
        var variants = new[] { "crate", " crate ", "CRATE", "crate  " };
        var rows = Enumerable.Range(0, 50).Select(index => Row(index + 2, unitTerm: variants[index % variants.Length])).ToArray();

        var result = await ResolveAsync(rows);

        Assert.Equal(50, result.Errors.Count);
        Assert.Equal(1, _references.UnitResolutionCount);

        // The resolver's own suggestion cache must also collapse the same normalized term to one
        // lookup, not just the reference-identity cache: a file naming one unknown Unit five thousand
        // times must not fetch suggestions five thousand times.
        Assert.Equal(1, _catalog.SuggestionCount);
    }

    [Fact]
    public async Task An_unknown_Location_name_is_looked_up_once_however_many_rows_share_it_because_a_negative_result_is_cached_too()
    {
        var variants = new[] { "Bay 9", "  bay 9", "BAY   9", "bay 9 " };
        var rows = Enumerable.Range(0, 50)
            .Select(index => Row(index + 2, locationName: variants[index % variants.Length]))
            .ToArray();

        var result = await ResolveAsync(rows);

        Assert.Equal(50, result.Errors.Count);
        Assert.Equal(1, _references.LocationResolutionCount);
        Assert.Equal(1, _catalog.SuggestionCount);
    }

    [Fact]
    public async Task The_same_term_is_one_lookup_however_its_internal_whitespace_or_case_is_written()
    {
        var result = await ResolveAsync(
            Row(2, unitTerm: "each"),
            Row(3, unitTerm: " each "),
            Row(4, unitTerm: "EACH"),
            Row(5, unitTerm: "each  "));

        Assert.Empty(result.Errors);
        Assert.Equal(4, result.Rows.Count);
        Assert.Equal(1, _references.UnitResolutionCount);
        Assert.Equal(1, _references.UnitCanonicalNameLookupCount);
    }

    [Fact]
    public async Task The_same_Location_name_is_one_lookup_however_its_internal_whitespace_or_case_is_written()
    {
        var result = await ResolveAsync(
            Row(2, locationName: "Shelf A"),
            Row(3, locationName: "  shelf a"),
            Row(4, locationName: "SHELF   A"),
            Row(5, locationName: "shelf a "));

        Assert.Empty(result.Errors);
        Assert.Equal(4, result.Rows.Count);
        Assert.Equal(1, _references.LocationResolutionCount);
        Assert.Equal(1, _references.LocationNameLookupCount);
    }

    [Fact]
    public async Task A_row_that_resolves_carries_everything_the_merge_needs()
    {
        var result = await ResolveAsync(Row(7, name: "Steel Bolts", unitTerm: "bx", locationName: "Shelf A", note: "Blue box"));

        var row = Assert.Single(result.Rows);
        Assert.Equal(7, row.LineNumber);
        Assert.Equal("Steel Bolts", row.Name);
        Assert.Equal("steel bolts", row.NormalizedName);
        Assert.Equal("4", row.Quantity.ToInvariantText());
        Assert.Equal(_boxId, row.UnitId);
        Assert.Equal(_shelfId, row.LocationId);
        Assert.Equal("Blue box", row.Note);
    }

    [Fact]
    public async Task Resolving_never_creates_a_Unit_or_a_Location_however_many_rows_are_unknown()
    {
        await ResolveAsync(Row(2, unitTerm: "crate"), Row(3, locationName: "Bay 9"));

        Assert.Null(await _references.ResolveUnitAsync(_inventoryId, "crate", CancellationToken.None));
        Assert.Null(await _references.ResolveLocationAsync(_inventoryId, "Bay 9", CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_propagates_out_of_resolution()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Resolver().ResolveAsync(_inventoryId, [Row()], cancelled.Token));
    }
}
