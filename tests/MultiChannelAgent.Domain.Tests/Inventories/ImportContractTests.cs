using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Domain.Tests.Inventories;

public class ImportContractTests
{
    [Fact]
    public void The_file_contract_is_exactly_five_columns_in_one_fixed_order() =>
        Assert.Equal(["Name", "Quantity", "Unit", "Location", "Note"], ImportContract.Headers);

    [Fact]
    public void Every_bound_the_specification_states_is_stated_here()
    {
        Assert.Equal(2 * 1024 * 1024, ImportContract.MaxUploadBytes);
        Assert.Equal(5_000, ImportContract.MaxSourceRows);
        Assert.Equal(5_000, ImportContract.MaxNormalizedEntries);
        Assert.Equal(500, ImportContract.MaxReportedErrors);
    }

    [Fact]
    public void Every_column_constant_is_its_zero_based_position()
    {
        Assert.Equal(0, ImportContract.NameColumn);
        Assert.Equal(1, ImportContract.QuantityColumn);
        Assert.Equal(2, ImportContract.UnitColumn);
        Assert.Equal(3, ImportContract.LocationColumn);
        Assert.Equal(4, ImportContract.NoteColumn);
    }

    [Fact]
    public void Every_column_constant_names_its_own_header_in_Headers()
    {
        Assert.Equal("Name", ImportContract.Headers[ImportContract.NameColumn]);
        Assert.Equal("Quantity", ImportContract.Headers[ImportContract.QuantityColumn]);
        Assert.Equal("Unit", ImportContract.Headers[ImportContract.UnitColumn]);
        Assert.Equal("Location", ImportContract.Headers[ImportContract.LocationColumn]);
        Assert.Equal("Note", ImportContract.Headers[ImportContract.NoteColumn]);
    }

    [Theory]
    [InlineData(ImportErrorCode.UnknownColumn, "unknown_column")]
    [InlineData(ImportErrorCode.DuplicateColumn, "duplicate_column")]
    [InlineData(ImportErrorCode.WrongColumnCount, "wrong_column_count")]
    [InlineData(ImportErrorCode.InvalidEncoding, "invalid_encoding")]
    [InlineData(ImportErrorCode.UnterminatedQuote, "unterminated_quote")]
    [InlineData(ImportErrorCode.MalformedQuote, "malformed_quote")]
    [InlineData(ImportErrorCode.TooFewFields, "too_few_fields")]
    [InlineData(ImportErrorCode.TooManyFields, "too_many_fields")]
    [InlineData(ImportErrorCode.MissingName, "missing_name")]
    [InlineData(ImportErrorCode.MissingQuantity, "missing_quantity")]
    [InlineData(ImportErrorCode.InvalidQuantity, "invalid_quantity")]
    [InlineData(ImportErrorCode.QuantityOverflow, "quantity_overflow")]
    [InlineData(ImportErrorCode.NameTooLong, "name_too_long")]
    [InlineData(ImportErrorCode.NoteTooLong, "note_too_long")]
    [InlineData(ImportErrorCode.UnitTooLong, "unit_too_long")]
    [InlineData(ImportErrorCode.LocationTooLong, "location_too_long")]
    [InlineData(ImportErrorCode.UnknownUnit, "unknown_unit")]
    [InlineData(ImportErrorCode.UnknownLocation, "unknown_location")]
    [InlineData(ImportErrorCode.ConflictingNotes, "conflicting_notes")]
    [InlineData(ImportErrorCode.FileTooLarge, "file_too_large")]
    [InlineData(ImportErrorCode.TooManyRows, "too_many_rows")]
    [InlineData(ImportErrorCode.TooManyEntries, "too_many_entries")]
    [InlineData(ImportErrorCode.EmptyFile, "empty_file")]
    public void Every_error_has_stable_machine_text_that_round_trips(ImportErrorCode code, string text)
    {
        Assert.Equal(text, ImportFacts.ToMachineText(code));
        Assert.True(ImportFacts.TryParse(text, out var parsed));
        Assert.Equal(code, parsed);
    }

    [Fact]
    public void Every_error_code_has_non_empty_distinct_machine_text_that_round_trips()
    {
        var codes = Enum.GetValues<ImportErrorCode>();
        var texts = codes.Select(ImportFacts.ToMachineText).ToList();

        Assert.All(texts, text => Assert.False(string.IsNullOrEmpty(text)));
        Assert.Equal(texts.Count, texts.Distinct(StringComparer.Ordinal).Count());

        foreach (var code in codes)
        {
            Assert.True(ImportFacts.TryParse(ImportFacts.ToMachineText(code), out var parsed));
            Assert.Equal(code, parsed);
        }
    }

    [Fact]
    public void Machine_text_is_exact_and_case_sensitive()
    {
        Assert.False(ImportFacts.TryParse("Unknown_Column", out _));
        Assert.False(ImportFacts.TryParse("unknown", out _));
        Assert.False(ImportFacts.TryParse(null, out _));
    }

    [Fact]
    public void A_digest_is_sixty_four_lowercase_hexadecimal_characters()
    {
        var digest = FileDigest.Of([1, 2, 3]);

        Assert.Equal(64, digest.Value.Length);
        Assert.Equal(digest.Value, digest.Value.ToLowerInvariant());
        Assert.Equal(digest, FileDigest.Of([1, 2, 3]));
        Assert.NotEqual(digest, FileDigest.Of([1, 2, 4]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("ZZ00000000000000000000000000000000000000000000000000000000000000")]
    public void A_malformed_digest_is_refused(string value) => Assert.False(FileDigest.TryParse(value, out _));

    [Fact]
    public void A_well_formed_digest_round_trips_through_its_text()
    {
        var digest = FileDigest.Of("Name,Quantity,Unit,Location,Note\n"u8.ToArray());

        Assert.True(FileDigest.TryParse(digest.Value, out var parsed));
        Assert.Equal(digest, parsed);
    }

    [Fact]
    public void An_import_audits_one_minimal_fact() =>
        Assert.Equal("Import:Completed", ImportFacts.CompletedOutcomeCode);
}
