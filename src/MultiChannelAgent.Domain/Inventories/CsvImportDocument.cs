using System.Text;

namespace MultiChannelAgent.Domain.Inventories;

/// <summary>One data record, with the source line it came from so every later error can point at it.</summary>
public sealed record CsvImportRecord(int LineNumber, IReadOnlyList<string> Fields);

/// <summary>A header-validated file: the records, in file order.</summary>
public sealed record CsvImportDocumentContent(IReadOnlyList<CsvImportRecord> Records);

/// <summary>
/// The outcome of reading bytes. Exactly one of the two is present: a document when the file's
/// envelope is sound, or the errors that stopped it. Row-level meaning is somebody else's job.
/// </summary>
public sealed record CsvImportReadResult(CsvImportDocumentContent? Document, IReadOnlyList<ImportRowError> Errors);

/// <summary>
/// The only thing in this system that understands CSV bytes.
///
/// It decodes strict UTF-8 (a BOM is accepted and stripped), splits records on CRLF, LF, or bare CR,
/// honours RFC 4180 quoting, checks that the five headers are present in their fixed order, and
/// splits each record into exactly five fields. It knows nothing about Inventories, Units, Locations,
/// or Quantity - which is what lets every encoding and quoting rule be reasoned about on its own.
///
/// Failures of the file's envelope - encoding, quoting, headers, bounds - stop the read, because rows
/// interpreted against a misaligned or unreadable file would be noise rather than help. Failures of a
/// single record's shape are collected and the read continues, because those are exactly the errors a
/// Participant wants to see all at once.
/// </summary>
public static class CsvImportDocument
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static CsvImportReadResult Read(ReadOnlySpan<byte> content)
    {
        if (content.Length > ImportContract.MaxUploadBytes)
        {
            return Failed(ImportErrorCode.FileTooLarge, lineNumber: 0, columnIndex: null);
        }

        // The digest is taken over the bytes as received; the BOM is only in the way of the text.
        if (content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF)
        {
            content = content[3..];
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(content);
        }
        catch (DecoderFallbackException)
        {
            return Failed(ImportErrorCode.InvalidEncoding, lineNumber: 0, columnIndex: null);
        }

        if (!TrySplitRecords(text, out var rawRecords, out var envelopeError))
        {
            return new CsvImportReadResult(null, [envelopeError!]);
        }

        if (rawRecords.Count == 0)
        {
            return Failed(ImportErrorCode.EmptyFile, lineNumber: 0, columnIndex: null);
        }

        var headerErrors = ValidateHeader(rawRecords[0]);
        if (headerErrors.Count > 0)
        {
            return new CsvImportReadResult(null, headerErrors);
        }

        if (rawRecords.Count == 1)
        {
            return Failed(ImportErrorCode.EmptyFile, lineNumber: 0, columnIndex: null);
        }

        if (rawRecords.Count - 1 > ImportContract.MaxSourceRows)
        {
            return Failed(ImportErrorCode.TooManyRows, lineNumber: 0, columnIndex: null);
        }

        var records = new List<CsvImportRecord>(rawRecords.Count - 1);
        var errors = new List<ImportRowError>();

        for (var i = 1; i < rawRecords.Count; i++)
        {
            var raw = rawRecords[i];

            if (raw.Fields.Count < ImportContract.Headers.Count)
            {
                errors.Add(new ImportRowError(ImportErrorCode.TooFewFields, raw.LineNumber, null));
                continue;
            }

            if (raw.Fields.Count > ImportContract.Headers.Count)
            {
                errors.Add(new ImportRowError(ImportErrorCode.TooManyFields, raw.LineNumber, null));
                continue;
            }

            records.Add(new CsvImportRecord(raw.LineNumber, raw.Fields));
        }

        return new CsvImportReadResult(new CsvImportDocumentContent(records), errors);
    }

    private static IReadOnlyList<ImportRowError> ValidateHeader(CsvImportRecord header)
    {
        if (header.Fields.Count != ImportContract.Headers.Count)
        {
            return [new ImportRowError(ImportErrorCode.WrongColumnCount, header.LineNumber, null)];
        }

        var errors = new List<ImportRowError>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var column = 0; column < header.Fields.Count; column++)
        {
            var name = header.Fields[column].Trim();

            if (!seen.Add(name))
            {
                errors.Add(new ImportRowError(ImportErrorCode.DuplicateColumn, header.LineNumber, column));
                continue;
            }

            // The order is part of the contract, so a header is only right in its own position.
            if (!string.Equals(name, ImportContract.Headers[column], StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(new ImportRowError(ImportErrorCode.UnknownColumn, header.LineNumber, column));
            }
        }

        return errors;
    }

    /// <summary>
    /// Splits the whole text into records and fields in one pass, tracking the source line so a record
    /// spanning a quoted newline still reports the line it started on.
    /// </summary>
    private static bool TrySplitRecords(string text, out List<CsvImportRecord> records, out ImportRowError? error)
    {
        records = [];
        error = null;

        var fields = new List<string>(ImportContract.Headers.Count);
        var field = new StringBuilder();
        var line = 1;
        var recordLine = 1;
        var quoted = false;
        var closedQuote = false;
        var started = false;
        var index = 0;

        while (index < text.Length)
        {
            var character = text[index];

            if (quoted)
            {
                if (character == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"')
                    {
                        field.Append('"');
                        index += 2;
                        continue;
                    }

                    quoted = false;
                    closedQuote = true;
                    index++;
                    continue;
                }

                // A quoted field may embed CRLF, LF, or a bare CR. Whichever spelling appears, it is one
                // physical newline: the field keeps the literal bytes, but the source line advances by
                // exactly one, so a record after it still reports its true line rather than an inflated one.
                if (character == '\r')
                {
                    field.Append('\r');
                    index++;
                    if (index < text.Length && text[index] == '\n')
                    {
                        field.Append('\n');
                        index++;
                    }

                    line++;
                    continue;
                }

                if (character == '\n')
                {
                    line++;
                }

                field.Append(character);
                index++;
                continue;
            }

            if (character == '"' && field.Length == 0 && !closedQuote)
            {
                quoted = true;
                started = true;
                index++;
                continue;
            }

            if (character == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
                closedQuote = false;
                started = true;
                index++;
                continue;
            }

            if (character is '\r' or '\n')
            {
                fields.Add(field.ToString());
                field.Clear();
                records.Add(new CsvImportRecord(recordLine, fields));
                fields = new List<string>(ImportContract.Headers.Count);
                closedQuote = false;
                started = false;

                // CRLF is one record end, not two.
                if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
                {
                    index++;
                }

                index++;
                line++;
                recordLine = line;
                continue;
            }

            if (closedQuote)
            {
                error = new ImportRowError(ImportErrorCode.MalformedQuote, recordLine, null);
                return false;
            }

            field.Append(character);
            started = true;
            index++;
        }

        if (quoted)
        {
            error = new ImportRowError(ImportErrorCode.UnterminatedQuote, recordLine, null);
            return false;
        }

        // A trailing newline already closed the last record; anything else still in hand is one more.
        if (started || field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            records.Add(new CsvImportRecord(recordLine, fields));
        }

        return true;
    }

    private static CsvImportReadResult Failed(ImportErrorCode code, int lineNumber, int? columnIndex) =>
        new(null, [new ImportRowError(code, lineNumber, columnIndex)]);
}
