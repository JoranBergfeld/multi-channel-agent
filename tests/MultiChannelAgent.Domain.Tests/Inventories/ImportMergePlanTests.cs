using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Domain.Tests.Inventories;

public class ImportMergePlanTests
{
    private static readonly UnitId Each = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly UnitId Box = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly LocationId ShelfA = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));

    private static ResolvedImportRow Row(
        int lineNumber,
        string name = "Steel Bolts",
        string quantity = "1",
        UnitId? unitId = null,
        LocationId? locationId = null,
        string? note = null) => new()
        {
            LineNumber = lineNumber,
            Name = name,
            NormalizedName = NameNormalization.Normalize(name),
            Quantity = ParseQuantity(quantity),
            UnitId = unitId ?? Each,
            UnitCanonicalName = "each",
            LocationId = locationId,
            LocationName = locationId is null ? null : "Shelf A",
            Note = note,
        };

    private static Quantity ParseQuantity(string text)
    {
        Assert.True(Quantity.TryParseInvariant(text, out var quantity));
        return quantity;
    }

    [Fact]
    public void Rows_that_are_not_equivalent_each_become_their_own_entry()
    {
        var plan = ImportMergePlan.Create(
        [
            Row(2, name: "Steel Bolts"),
            Row(3, name: "Brass Rivets"),
            Row(4, name: "Steel Bolts", unitId: Box),
            Row(5, name: "Steel Bolts", locationId: ShelfA),
        ]);

        Assert.Empty(plan.Errors);
        Assert.Equal(4, plan.Entries.Count);
    }

    [Fact]
    public void Equivalent_rows_sum_their_Quantity_and_report_every_line_that_contributed()
    {
        var plan = ImportMergePlan.Create([Row(2, quantity: "4"), Row(5, quantity: "6.5")]);

        Assert.Empty(plan.Errors);
        var entry = Assert.Single(plan.Entries);
        Assert.Equal("10.5", entry.Quantity.ToInvariantText());
        Assert.Equal([2, 5], entry.SourceLineNumbers);
        Assert.Equal(2, entry.LineNumber);
    }

    [Fact]
    public void Equivalence_ignores_case_and_whitespace_exactly_as_the_domain_does()
    {
        var plan = ImportMergePlan.Create([Row(2, name: "Steel Bolts"), Row(3, name: "  STEEL   bolts ")]);

        var entry = Assert.Single(plan.Entries);
        Assert.Equal("2", entry.Quantity.ToInvariantText());

        // The first line's display text is what survives, so the preview shows what a person wrote.
        Assert.Equal("Steel Bolts", entry.Name);
    }

    [Fact]
    public void A_blank_Note_is_compatible_with_anything_and_the_written_one_survives()
    {
        var plan = ImportMergePlan.Create([Row(2, note: null), Row(3, note: "Blue box"), Row(4, note: null)]);

        Assert.Empty(plan.Errors);
        Assert.Equal("Blue box", Assert.Single(plan.Entries).Note);
    }

    [Fact]
    public void The_same_Note_written_twice_is_compatible()
    {
        var plan = ImportMergePlan.Create([Row(2, note: "Blue box"), Row(3, note: "Blue box")]);

        Assert.Empty(plan.Errors);
        Assert.Equal("Blue box", Assert.Single(plan.Entries).Note);
    }

    [Fact]
    public void Two_different_Notes_on_equivalent_rows_are_refused_rather_than_guessed()
    {
        var plan = ImportMergePlan.Create([Row(2, note: "Blue box"), Row(3, note: "Red box")]);

        var error = Assert.Single(plan.Errors);
        Assert.Equal(ImportErrorCode.ConflictingNotes, error.Code);
        Assert.Equal(3, error.LineNumber);
        Assert.Equal(ImportContract.NoteColumn, error.ColumnIndex);
        Assert.Empty(plan.Entries);
    }

    [Fact]
    public void Notes_differing_only_in_case_are_a_conflict_because_a_Note_is_what_someone_wrote()
    {
        var plan = ImportMergePlan.Create([Row(2, note: "Blue box"), Row(3, note: "blue box")]);

        Assert.Equal(ImportErrorCode.ConflictingNotes, Assert.Single(plan.Errors).Code);
    }

    [Fact]
    public void Every_conflicting_line_after_the_first_is_named_so_one_pass_fixes_the_file()
    {
        var plan = ImportMergePlan.Create([Row(2, note: "A"), Row(3, note: "B"), Row(4, note: "C")]);

        Assert.Equal([3, 4], plan.Errors.Select(error => error.LineNumber));
    }

    [Fact]
    public void A_sum_that_leaves_the_Quantity_bounds_is_refused_against_the_first_line_of_the_group()
    {
        var huge = new string('9', Quantity.MaxIntegerDigits);
        var plan = ImportMergePlan.Create([Row(2, quantity: huge), Row(3, quantity: huge)]);

        var error = Assert.Single(plan.Errors);
        Assert.Equal(ImportErrorCode.QuantityOverflow, error.Code);
        Assert.Equal(2, error.LineNumber);
    }

    [Fact]
    public void More_normalized_entries_than_the_bound_is_one_whole_file_error()
    {
        var rows = Enumerable
            .Range(0, ImportContract.MaxNormalizedEntries + 1)
            .Select(index => Row(index + 2, name: $"Item {index}"))
            .ToList();

        var plan = ImportMergePlan.Create(rows);

        var error = Assert.Single(plan.Errors);
        Assert.Equal(ImportErrorCode.TooManyEntries, error.Code);
        Assert.Equal(0, error.LineNumber);
        Assert.Empty(plan.Entries);
    }

    [Fact]
    public void Entries_come_back_in_the_order_their_first_line_appeared()
    {
        var plan = ImportMergePlan.Create([Row(4, name: "Zinc"), Row(2, name: "Alpha"), Row(3, name: "Zinc")]);

        Assert.Equal(["Zinc", "Alpha"], plan.Entries.Select(entry => entry.Name));
    }
}
