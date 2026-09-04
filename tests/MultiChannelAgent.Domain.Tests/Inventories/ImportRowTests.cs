using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Domain.Tests.Inventories;

public class ImportRowTests
{
    private static CsvImportRecord Record(
        string name = "Steel Bolts",
        string quantity = "10",
        string unit = "each",
        string location = "Shelf A",
        string note = "Blue box",
        int lineNumber = 2) => new(lineNumber, [name, quantity, unit, location, note]);

    [Fact]
    public void A_complete_row_carries_its_tidy_display_text_and_its_normalized_name()
    {
        Assert.True(ImportRow.TryCreate(Record(name: "  Steel   Bolts  "), out var row, out var errors));

        Assert.Empty(errors);
        Assert.Equal(2, row!.LineNumber);
        Assert.Equal("Steel Bolts", row.Name);
        Assert.Equal("steel bolts", row.NormalizedName);
        Assert.Equal("10", row.Quantity.ToInvariantText());
        Assert.Equal("each", row.UnitTerm);
        Assert.Equal("Shelf A", row.LocationName);
        Assert.Equal("Blue box", row.Note);
    }

    [Fact]
    public void A_blank_Unit_means_the_reserved_each_Unit()
    {
        Assert.True(ImportRow.TryCreate(Record(unit: "   "), out var row, out _));

        Assert.Equal(Unit.ReservedEachCanonicalName, row!.UnitTerm);
    }

    [Fact]
    public void A_blank_Location_means_unlocated_and_a_blank_Note_means_no_Note()
    {
        Assert.True(ImportRow.TryCreate(Record(location: "", note: "   "), out var row, out _));

        Assert.Null(row!.LocationName);
        Assert.Null(row.Note);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_row_without_a_Name_is_refused_at_the_Name_column(string name)
    {
        Assert.False(ImportRow.TryCreate(Record(name: name), out _, out var errors));

        var error = Assert.Single(errors);
        Assert.Equal(ImportErrorCode.MissingName, error.Code);
        Assert.Equal(ImportContract.NameColumn, error.ColumnIndex);
        Assert.Equal(2, error.LineNumber);
    }

    [Fact]
    public void A_row_without_a_Quantity_is_refused_at_the_Quantity_column()
    {
        Assert.False(ImportRow.TryCreate(Record(quantity: " "), out _, out var errors));

        Assert.Equal(ImportErrorCode.MissingQuantity, Assert.Single(errors).Code);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1.2.3")]
    [InlineData("1,5")]
    [InlineData("1e3")]
    [InlineData("ten")]
    [InlineData("$5")]
    [InlineData("1 000")]
    public void A_Quantity_that_is_not_an_exact_invariant_non_negative_decimal_is_refused(string quantity)
    {
        Assert.False(ImportRow.TryCreate(Record(quantity: quantity), out _, out var errors));

        var error = Assert.Single(errors);
        Assert.Equal(ImportErrorCode.InvalidQuantity, error.Code);
        Assert.Equal(ImportContract.QuantityColumn, error.ColumnIndex);
    }

    [Fact]
    public void Zero_is_a_perfectly_good_starting_Quantity()
    {
        Assert.True(ImportRow.TryCreate(Record(quantity: "0"), out var row, out _));

        Assert.Equal(Quantity.Zero, row!.Quantity);
    }

    [Fact]
    public void Every_length_bound_is_reported_against_its_own_column()
    {
        Assert.False(ImportRow.TryCreate(Record(name: new string('a', StockEntry.MaxNameLength + 1)), out _, out var name));
        Assert.Equal(ImportErrorCode.NameTooLong, Assert.Single(name).Code);
        Assert.Equal(ImportContract.NameColumn, name[0].ColumnIndex);

        Assert.False(ImportRow.TryCreate(Record(note: new string('a', StockEntry.MaxNoteLength + 1)), out _, out var note));
        Assert.Equal(ImportErrorCode.NoteTooLong, Assert.Single(note).Code);
        Assert.Equal(ImportContract.NoteColumn, note[0].ColumnIndex);

        Assert.False(ImportRow.TryCreate(Record(unit: new string('a', Unit.MaxNameLength + 1)), out _, out var unit));
        Assert.Equal(ImportErrorCode.UnitTooLong, Assert.Single(unit).Code);
        Assert.Equal(ImportContract.UnitColumn, unit[0].ColumnIndex);

        Assert.False(ImportRow.TryCreate(Record(location: new string('a', Location.MaxNameLength + 1)), out _, out var location));
        Assert.Equal(ImportErrorCode.LocationTooLong, Assert.Single(location).Code);
        Assert.Equal(ImportContract.LocationColumn, location[0].ColumnIndex);
    }

    [Fact]
    public void Every_thing_wrong_with_one_row_is_reported_together()
    {
        Assert.False(ImportRow.TryCreate(Record(name: "", quantity: "nope"), out _, out var errors));

        Assert.Equal(
            [ImportErrorCode.MissingName, ImportErrorCode.InvalidQuantity],
            errors.Select(error => error.Code));
    }

    [Fact]
    public void Every_thing_wrong_with_one_row_accumulates_in_column_order_regardless_of_field_order()
    {
        Assert.False(
            ImportRow.TryCreate(
                Record(
                    name: "",
                    quantity: "nope",
                    unit: new string('a', Unit.MaxNameLength + 1),
                    location: new string('a', Location.MaxNameLength + 1),
                    note: new string('a', StockEntry.MaxNoteLength + 1)),
                out _,
                out var errors));

        Assert.Equal(
            [
                ImportErrorCode.MissingName,
                ImportErrorCode.InvalidQuantity,
                ImportErrorCode.UnitTooLong,
                ImportErrorCode.LocationTooLong,
                ImportErrorCode.NoteTooLong,
            ],
            errors.Select(error => error.Code));
    }

    [Fact]
    public void Every_error_on_a_row_carries_that_rows_source_line()
    {
        Assert.False(ImportRow.TryCreate(Record(name: "", quantity: "nope", lineNumber: 41), out _, out var errors));

        Assert.All(errors, error => Assert.Equal(41, error.LineNumber));
    }

    [Fact]
    public void A_null_record_is_refused_as_an_argument_rather_than_read()
    {
        Assert.Throws<ArgumentNullException>(() => ImportRow.TryCreate(null!, out _, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(6)]
    public void A_record_that_does_not_carry_exactly_five_fields_is_refused_as_an_argument_rather_than_indexed_out_of_range(int fieldCount)
    {
        var malformed = new CsvImportRecord(2, Enumerable.Repeat("x", fieldCount).ToList());

        Assert.Throws<ArgumentException>(() => ImportRow.TryCreate(malformed, out _, out _));
    }
}
