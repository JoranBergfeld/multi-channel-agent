using System.Text;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Domain.Tests.Inventories;

public class CsvImportDocumentTests
{
    private const string Header = "Name,Quantity,Unit,Location,Note";

    private static CsvImportReadResult Read(string text) => CsvImportDocument.Read(Encoding.UTF8.GetBytes(text));

    private static CsvImportReadResult ReadBytes(byte[] bytes) => CsvImportDocument.Read(bytes);

    [Fact]
    public void A_well_formed_file_yields_one_record_per_data_line_with_its_source_line_number()
    {
        var result = Read($"{Header}\r\nSteel Bolts,10,each,Shelf A,Blue box\r\nBrass Rivets,2,,,\r\n");

        Assert.Empty(result.Errors);
        Assert.Equal(2, result.Document!.Records.Count);
        Assert.Equal(2, result.Document.Records[0].LineNumber);
        Assert.Equal(["Steel Bolts", "10", "each", "Shelf A", "Blue box"], result.Document.Records[0].Fields);
        Assert.Equal(3, result.Document.Records[1].LineNumber);
        Assert.Equal(["Brass Rivets", "2", "", "", ""], result.Document.Records[1].Fields);
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    [InlineData("\r")]
    public void Every_newline_a_spreadsheet_might_write_ends_a_record(string newline)
    {
        var result = Read($"{Header}{newline}Steel Bolts,10,each,Shelf A,{newline}");

        Assert.Empty(result.Errors);
        Assert.Single(result.Document!.Records);
    }

    [Fact]
    public void A_trailing_newline_does_not_invent_an_empty_final_record()
    {
        Assert.Single(Read($"{Header}\nSteel Bolts,10,each,,\n").Document!.Records);
        Assert.Single(Read($"{Header}\nSteel Bolts,10,each,,").Document!.Records);
    }

    [Fact]
    public void A_leading_byte_order_mark_is_accepted_and_stripped()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes($"{Header}\nA,1,,,")).ToArray();

        var result = ReadBytes(bytes);

        Assert.Empty(result.Errors);
        Assert.Single(result.Document!.Records);
    }

    [Fact]
    public void Bytes_that_are_not_valid_UTF8_are_one_legible_error_rather_than_mangled_text()
    {
        var result = ReadBytes([0xFF, 0xFE, 0x41, 0x00]);

        Assert.Equal(ImportErrorCode.InvalidEncoding, Assert.Single(result.Errors).Code);
        Assert.Null(result.Document);
    }

    [Fact]
    public void A_quoted_field_may_carry_commas_newlines_and_escaped_quotes()
    {
        var result = Read($"{Header}\n\"Bolts, 5mm\",10,each,,\"He said \"\"hi\"\"\nsecond line\"\n");

        Assert.Empty(result.Errors);
        var record = Assert.Single(result.Document!.Records);
        Assert.Equal(2, record.LineNumber);
        Assert.Equal("Bolts, 5mm", record.Fields[0]);
        Assert.Equal("He said \"hi\"\nsecond line", record.Fields[4]);
    }

    [Fact]
    public void A_quoted_embedded_CRLF_counts_as_one_line_not_two()
    {
        var result = Read($"{Header}\n\"Line1\r\nLine2\",1,,,\nB,2,,,\n");

        Assert.Empty(result.Errors);
        Assert.Equal(2, result.Document!.Records.Count);
        Assert.Equal(2, result.Document.Records[0].LineNumber);
        Assert.Equal("Line1\r\nLine2", result.Document.Records[0].Fields[0]);
        Assert.Equal(4, result.Document.Records[1].LineNumber);
    }

    [Fact]
    public void A_quoted_embedded_bare_CR_still_counts_as_one_line()
    {
        var result = Read($"{Header}\n\"Line1\rLine2\",1,,,\nB,2,,,\n");

        Assert.Empty(result.Errors);
        Assert.Equal(2, result.Document!.Records.Count);
        Assert.Equal(2, result.Document.Records[0].LineNumber);
        Assert.Equal("Line1\rLine2", result.Document.Records[0].Fields[0]);
        Assert.Equal(4, result.Document.Records[1].LineNumber);
    }

    [Fact]
    public void A_quote_inside_an_unquoted_field_is_literal_data()
    {
        var result = Read($"{Header}\n5\" pipe,10,each,,\n");

        Assert.Empty(result.Errors);
        Assert.Equal("5\" pipe", result.Document!.Records[0].Fields[0]);
    }

    [Fact]
    public void A_quoted_field_that_never_closes_is_refused()
    {
        var result = Read($"{Header}\n\"Steel Bolts,10,each,,\n");

        Assert.Equal(ImportErrorCode.UnterminatedQuote, Assert.Single(result.Errors).Code);
        Assert.Null(result.Document);
    }

    [Fact]
    public void A_closing_quote_followed_by_stray_text_is_refused()
    {
        var result = Read($"{Header}\n\"Steel\" Bolts,10,each,,\n");

        Assert.Equal(ImportErrorCode.MalformedQuote, Assert.Single(result.Errors).Code);
        Assert.Null(result.Document);
    }

    [Theory]
    [InlineData("name,quantity,unit,location,note")]
    [InlineData("NAME,QUANTITY,UNIT,LOCATION,NOTE")]
    [InlineData(" Name , Quantity , Unit , Location , Note ")]
    public void Headers_are_matched_without_case_or_surrounding_whitespace(string header)
    {
        var result = Read($"{header}\nA,1,,,\n");

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void A_header_that_is_not_one_of_the_five_is_named()
    {
        var result = Read("Name,Quantity,Unit,Location,Colour\nA,1,,,\n");

        var error = Assert.Single(result.Errors);
        Assert.Equal(ImportErrorCode.UnknownColumn, error.Code);
        Assert.Equal(1, error.LineNumber);
        Assert.Equal(4, error.ColumnIndex);
        Assert.Null(result.Document);
    }

    [Fact]
    public void A_repeated_header_is_refused()
    {
        var result = Read("Name,Quantity,Unit,Note,Note\nA,1,,,\n");

        Assert.Contains(result.Errors, error => error.Code == ImportErrorCode.DuplicateColumn);
        Assert.Null(result.Document);
    }

    [Theory]
    [InlineData("Name,Quantity,Unit,Location")]
    [InlineData("Name,Quantity,Unit,Location,Note,Extra")]
    public void A_file_without_exactly_five_headers_is_refused(string header)
    {
        var result = Read($"{header}\nA,1,,,\n");

        Assert.Equal(ImportErrorCode.WrongColumnCount, Assert.Single(result.Errors).Code);
        Assert.Null(result.Document);
    }

    [Fact]
    public void A_header_failure_stops_before_a_single_row_is_read()
    {
        var result = Read("Name,Quantity,Unit,Location,Colour\n,,,,\n,,,,\n");

        Assert.Single(result.Errors);
        Assert.Null(result.Document);
    }

    [Fact]
    public void A_row_with_the_wrong_number_of_fields_names_its_line()
    {
        var result = Read($"{Header}\nA,1,,\nB,1,,,,\n");

        Assert.Equal(2, result.Errors.Count);
        Assert.Equal(ImportErrorCode.TooFewFields, result.Errors[0].Code);
        Assert.Equal(2, result.Errors[0].LineNumber);
        Assert.Equal(ImportErrorCode.TooManyFields, result.Errors[1].Code);
        Assert.Equal(3, result.Errors[1].LineNumber);
    }

    [Fact]
    public void A_valid_row_survives_alongside_a_malformed_neighbour()
    {
        var result = Read($"{Header}\nGood Widget,1,each,Shelf A,\nBad,1,,\nAlso Good,2,each,,\n");

        var error = Assert.Single(result.Errors);
        Assert.Equal(ImportErrorCode.TooFewFields, error.Code);
        Assert.Equal(3, error.LineNumber);

        Assert.NotNull(result.Document);
        Assert.Equal(2, result.Document!.Records.Count);
        Assert.Equal(["Good Widget", "1", "each", "Shelf A", ""], result.Document.Records[0].Fields);
        Assert.Equal(["Also Good", "2", "each", "", ""], result.Document.Records[1].Fields);
    }

    [Fact]
    public void A_file_with_only_a_header_imports_nothing_and_says_so()
    {
        Assert.Equal(ImportErrorCode.EmptyFile, Assert.Single(Read($"{Header}\n").Errors).Code);
        Assert.Equal(ImportErrorCode.EmptyFile, Assert.Single(Read(string.Empty).Errors).Code);
    }

    [Fact]
    public void A_file_beyond_the_upload_bound_is_refused_by_the_domain_too()
    {
        var oversized = new byte[ImportContract.MaxUploadBytes + 1];

        Assert.Equal(ImportErrorCode.FileTooLarge, Assert.Single(ReadBytes(oversized).Errors).Code);
        Assert.Null(ReadBytes(oversized).Document);
    }

    [Fact]
    public void A_file_beyond_the_source_row_bound_is_refused()
    {
        var builder = new StringBuilder(Header).Append('\n');
        for (var row = 0; row < ImportContract.MaxSourceRows + 1; row++)
        {
            builder.Append("A,1,,,\n");
        }

        Assert.Equal(ImportErrorCode.TooManyRows, Assert.Single(Read(builder.ToString()).Errors).Code);
    }

    [Fact]
    public void A_file_far_beyond_the_row_bound_is_refused_without_materializing_every_record()
    {
        // A file of nothing but blank lines is within the byte cap yet holds roughly two million
        // one-byte records - far more than MaxSourceRows. Reading it must stop once the bound is
        // crossed rather than build a CsvImportRecord and a List<string> for every one of them: the
        // gap between "a few megabytes" and "hundreds of megabytes" is the row bound doing its job.
        var builder = new StringBuilder(Header).Append('\n');
        while (builder.Length < ImportContract.MaxUploadBytes - 1)
        {
            builder.Append('\n');
        }

        var bytes = Encoding.UTF8.GetBytes(builder.ToString());
        Assert.True(bytes.Length <= ImportContract.MaxUploadBytes);

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var result = CsvImportDocument.Read(bytes);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.Equal(ImportErrorCode.TooManyRows, Assert.Single(result.Errors).Code);
        Assert.Null(result.Document);
        Assert.True(
            allocated < 10 * 1024 * 1024,
            $"expected an allocation bounded by MaxSourceRows, but reading allocated {allocated:N0} bytes");
    }
}
