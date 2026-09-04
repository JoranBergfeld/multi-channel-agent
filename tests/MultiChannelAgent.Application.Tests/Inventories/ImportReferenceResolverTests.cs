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
        ResolveAsync(suggestionBudget: 10_000, rows);

    private Task<ImportResolutionResult> ResolveAsync(int suggestionBudget, params ImportRow[] rows) =>
        Resolver().ResolveAsync(_inventoryId, rows, suggestionBudget, CancellationToken.None);

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

        // The import path never makes a per-term round trip at all any more - not even a cached one -
        // it resolves every distinct term of a whole file in one batch call.
        Assert.Equal(0, _references.UnitResolutionCount);
        Assert.Equal(0, _references.UnitCanonicalNameLookupCount);
        Assert.Equal(1, _references.UnitBatchCallCount);
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
        Assert.Equal(0, _references.LocationResolutionCount);
        Assert.Equal(0, _references.LocationNameLookupCount);
        Assert.Equal(1, _references.LocationBatchCallCount);
    }

    [Fact]
    public async Task An_unknown_term_is_looked_up_once_however_many_rows_share_it_because_a_negative_result_is_cached_too()
    {
        var variants = new[] { "crate", " crate ", "CRATE", "crate  " };
        var rows = Enumerable.Range(0, 50).Select(index => Row(index + 2, unitTerm: variants[index % variants.Length])).ToArray();

        var result = await ResolveAsync(rows);

        Assert.Equal(50, result.Errors.Count);
        Assert.Equal(0, _references.UnitResolutionCount);
        Assert.Equal(1, _references.UnitBatchCallCount);

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
        Assert.Equal(0, _references.LocationResolutionCount);
        Assert.Equal(1, _references.LocationBatchCallCount);
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
        Assert.Equal(0, _references.UnitResolutionCount);
        Assert.Equal(0, _references.UnitCanonicalNameLookupCount);
        Assert.Equal(1, _references.UnitBatchCallCount);
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
        Assert.Equal(0, _references.LocationResolutionCount);
        Assert.Equal(0, _references.LocationNameLookupCount);
        Assert.Equal(1, _references.LocationBatchCallCount);
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
            () => Resolver().ResolveAsync(_inventoryId, [Row()], suggestionBudget: 10, cancelled.Token));
    }

    [Fact]
    public async Task A_row_that_resolves_survives_even_when_another_row_has_a_reference_error()
    {
        var result = await ResolveAsync(Row(2, unitTerm: "crate"), Row(3, unitTerm: "each"));

        var error = Assert.Single(result.Errors);
        Assert.Equal(ImportErrorCode.UnknownUnit, error.Code);
        Assert.Equal(2, error.LineNumber);

        var row = Assert.Single(result.Rows);
        Assert.Equal(3, row.LineNumber);
        Assert.Equal(_eachId, row.UnitId);
    }

    [Fact]
    public async Task Five_thousand_distinct_valid_Units_and_Locations_resolve_in_at_most_one_batch_call_each()
    {
        var rows = new ImportRow[5_000];
        for (var index = 0; index < rows.Length; index++)
        {
            var unitId = new UnitId(Guid.NewGuid());
            var locationId = new LocationId(Guid.NewGuid());
            var unitName = $"Unit {index}";
            var locationName = $"Location {index}";

            _references.AddUnit(_inventoryId, unitId, unitName);
            _references.AddLocation(_inventoryId, locationId, locationName);

            rows[index] = Row(index + 2, unitTerm: unitName, locationName: locationName);
        }

        var result = await ResolveAsync(rows);

        Assert.Empty(result.Errors);
        Assert.Equal(5_000, result.Rows.Count);

        // The root-cause fix: one batch call resolves a whole file's distinct terms, not one round
        // trip per distinct term - so a valid 5,000-row file with 5,000 distinct Units and Locations
        // costs two calls total, not up to 10,000.
        Assert.True(_references.UnitBatchCallCount <= 1);
        Assert.True(_references.LocationBatchCallCount <= 1);
        Assert.Equal(1, _references.UnitBatchCallCount);
        Assert.Equal(1, _references.LocationBatchCallCount);

        // No per-term call, cached or otherwise, is ever made from the import path any more.
        Assert.Equal(0, _references.UnitResolutionCount);
        Assert.Equal(0, _references.LocationResolutionCount);
        Assert.Equal(0, _references.UnitCanonicalNameLookupCount);
        Assert.Equal(0, _references.LocationNameLookupCount);
    }

    [Fact]
    public async Task Five_thousand_distinct_unknown_Units_and_Locations_resolve_in_at_most_one_batch_call_each()
    {
        var rows = Enumerable.Range(0, 5_000)
            .Select(index => Row(index + 2, unitTerm: $"unknown-unit-{index}", locationName: $"unknown-location-{index}"))
            .ToArray();

        var result = await ResolveAsync(suggestionBudget: 0, rows);

        Assert.Empty(result.Rows);
        Assert.Equal(10_000, result.Errors.Count);

        Assert.True(_references.UnitBatchCallCount <= 1);
        Assert.True(_references.LocationBatchCallCount <= 1);
        Assert.Equal(1, _references.UnitBatchCallCount);
        Assert.Equal(1, _references.LocationBatchCallCount);
        Assert.Equal(0, _references.UnitResolutionCount);
        Assert.Equal(0, _references.LocationResolutionCount);
    }

    [Fact]
    public async Task An_empty_Location_set_across_the_whole_file_never_reaches_the_Location_batch()
    {
        var rows = Enumerable.Range(0, 50).Select(index => Row(index + 2, locationName: null)).ToArray();

        var result = await ResolveAsync(rows);

        Assert.Empty(result.Errors);
        Assert.Equal(50, result.Rows.Count);
        Assert.Equal(1, _references.UnitBatchCallCount);
        Assert.Equal(0, _references.LocationBatchCallCount);
    }

    [Fact]
    public async Task A_suggestion_budget_bounds_catalog_lookups_while_every_unknown_term_still_reports()
    {
        var rows = Enumerable.Range(0, 5_000)
            .Select(index => Row(index + 2, unitTerm: $"unknown-unit-{index}"))
            .ToArray();

        var result = await ResolveAsync(suggestionBudget: ImportContract.MaxReportedErrors, rows);

        Assert.Equal(5_000, result.Errors.Count);
        Assert.True(_catalog.SuggestionCount <= ImportContract.MaxReportedErrors);
        Assert.Equal(ImportContract.MaxReportedErrors, _catalog.SuggestionCount);

        // Everything past the budget is still an exact, actionable error - just with no catalog round
        // trip behind it, so the count a Participant is told about is never a guess.
        Assert.All(
            result.Errors.Skip(ImportContract.MaxReportedErrors),
            error => Assert.Empty(error.Suggestions));
    }

    [Fact]
    public async Task A_repeated_unknown_term_still_costs_one_suggestion_query_however_small_the_budget()
    {
        var variants = new[] { "crate", " crate ", "CRATE", "crate  " };
        var rows = Enumerable.Range(0, 50).Select(index => Row(index + 2, unitTerm: variants[index % variants.Length])).ToArray();

        var result = await ResolveAsync(suggestionBudget: 3, rows);

        Assert.Equal(50, result.Errors.Count);
        Assert.Equal(1, _catalog.SuggestionCount);
        Assert.All(result.Errors, error => Assert.Empty(error.Suggestions));
    }

    [Fact]
    public async Task A_zero_suggestion_budget_makes_no_catalog_calls_at_all()
    {
        var result = await ResolveAsync(suggestionBudget: 0, Row(unitTerm: "crate"));

        var error = Assert.Single(result.Errors);
        Assert.Equal(ImportErrorCode.UnknownUnit, error.Code);
        Assert.Empty(error.Suggestions);
        Assert.Equal(0, _catalog.SuggestionCount);
    }

    [Fact]
    public async Task A_negative_suggestion_budget_is_refused()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Resolver().ResolveAsync(_inventoryId, [Row()], suggestionBudget: -1, CancellationToken.None));
    }
}
