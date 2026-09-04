namespace MultiChannelAgent.Domain.Inventories;

/// <summary>
/// The Initial Import file contract, as data. Every bound and every column name the specification
/// states lives here exactly once, because the parser, the merge, the application service, the HTTP
/// endpoint, and the web client all quote them.
/// </summary>
public static class ImportContract
{
    /// <summary>The five headers, in the one fixed order a file must present them in.</summary>
    public static readonly IReadOnlyList<string> Headers = ["Name", "Quantity", "Unit", "Location", "Note"];

    /// <summary>Two mebibytes of uploaded bytes.</summary>
    public const int MaxUploadBytes = 2 * 1024 * 1024;

    /// <summary>Data records, header excluded.</summary>
    public const int MaxSourceRows = 5_000;

    /// <summary>Stock Entries after equivalent rows have been merged.</summary>
    public const int MaxNormalizedEntries = 5_000;

    /// <summary>
    /// How many errors one answer carries. The promise is that a Participant can fix the file once,
    /// not that every one of five thousand broken rows is enumerated: beyond this the exact number
    /// omitted is reported instead, so nobody is misled about how much is left.
    /// </summary>
    public const int MaxReportedErrors = 500;

    /// <summary>The zero-based index of each column, so a row error can name the column it is about.</summary>
    public const int NameColumn = 0;
    public const int QuantityColumn = 1;
    public const int UnitColumn = 2;
    public const int LocationColumn = 3;
    public const int NoteColumn = 4;
}

/// <summary>
/// Every actionable thing that can be wrong with an import, as a closed set. There is deliberately no
/// free-text error: a Participant fixes a file by knowing which line, which column, and which rule.
/// </summary>
public enum ImportErrorCode
{
    /// <summary>A header that is not one of the five.</summary>
    UnknownColumn,

    /// <summary>The same header twice.</summary>
    DuplicateColumn,

    /// <summary>Fewer or more than five headers.</summary>
    WrongColumnCount,

    /// <summary>The bytes are not valid UTF-8.</summary>
    InvalidEncoding,

    /// <summary>A quoted field never closed before end of file.</summary>
    UnterminatedQuote,

    /// <summary>A closing quote followed by something other than a comma or a record end.</summary>
    MalformedQuote,

    TooFewFields,
    TooManyFields,
    MissingName,
    MissingQuantity,

    /// <summary>Not an invariant non-negative decimal within the shipped Quantity bounds.</summary>
    InvalidQuantity,

    /// <summary>Summing an equivalent group left the shipped Quantity bounds.</summary>
    QuantityOverflow,

    NameTooLong,
    NoteTooLong,
    UnitTooLong,
    LocationTooLong,

    /// <summary>No active Unit here answers to that term.</summary>
    UnknownUnit,

    /// <summary>No active Location here carries that name.</summary>
    UnknownLocation,

    /// <summary>Equivalent rows carried two different non-blank Notes.</summary>
    ConflictingNotes,

    FileTooLarge,
    TooManyRows,
    TooManyEntries,

    /// <summary>No data records at all - a header alone imports nothing and is almost certainly a mistake.</summary>
    EmptyFile,
}

/// <summary>
/// One actionable error, at one place. <see cref="LineNumber"/> is the 1-based source line - the
/// header is line 1 - and is 0 for a whole-file failure that belongs to no line.
/// <see cref="ColumnIndex"/> is the zero-based column from <see cref="ImportContract"/>, or null when
/// the error is about the record rather than one field.
///
/// Deliberately free of prose: the client renders a message from the code, so the same failure reads
/// the same way everywhere and nothing here has to be translated or kept in step with a UI string.
/// </summary>
public sealed record ImportRowError(ImportErrorCode Code, int LineNumber, int? ColumnIndex);

/// <summary>The one mapping from an import error to its machine text, and the one audit outcome code an import writes.</summary>
public static class ImportFacts
{
    /// <summary>The coarse outcome code a completed import is audited under. Never a file name, a digest, or a count.</summary>
    public const string CompletedOutcomeCode = "Import:Completed";

    public static string ToMachineText(ImportErrorCode code) => code switch
    {
        ImportErrorCode.UnknownColumn => "unknown_column",
        ImportErrorCode.DuplicateColumn => "duplicate_column",
        ImportErrorCode.WrongColumnCount => "wrong_column_count",
        ImportErrorCode.InvalidEncoding => "invalid_encoding",
        ImportErrorCode.UnterminatedQuote => "unterminated_quote",
        ImportErrorCode.MalformedQuote => "malformed_quote",
        ImportErrorCode.TooFewFields => "too_few_fields",
        ImportErrorCode.TooManyFields => "too_many_fields",
        ImportErrorCode.MissingName => "missing_name",
        ImportErrorCode.MissingQuantity => "missing_quantity",
        ImportErrorCode.InvalidQuantity => "invalid_quantity",
        ImportErrorCode.QuantityOverflow => "quantity_overflow",
        ImportErrorCode.NameTooLong => "name_too_long",
        ImportErrorCode.NoteTooLong => "note_too_long",
        ImportErrorCode.UnitTooLong => "unit_too_long",
        ImportErrorCode.LocationTooLong => "location_too_long",
        ImportErrorCode.UnknownUnit => "unknown_unit",
        ImportErrorCode.UnknownLocation => "unknown_location",
        ImportErrorCode.ConflictingNotes => "conflicting_notes",
        ImportErrorCode.FileTooLarge => "file_too_large",
        ImportErrorCode.TooManyRows => "too_many_rows",
        ImportErrorCode.TooManyEntries => "too_many_entries",
        ImportErrorCode.EmptyFile => "empty_file",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unhandled import error code."),
    };

    /// <summary>Reads machine text back. Exact and case-sensitive: text spelled differently is unreadable, not a near miss.</summary>
    public static bool TryParse(string? text, out ImportErrorCode code)
    {
        foreach (var candidate in Enum.GetValues<ImportErrorCode>())
        {
            if (string.Equals(ToMachineText(candidate), text, StringComparison.Ordinal))
            {
                code = candidate;
                return true;
            }
        }

        code = default;
        return false;
    }
}
