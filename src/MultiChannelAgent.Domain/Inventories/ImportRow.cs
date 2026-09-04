namespace MultiChannelAgent.Domain.Inventories;

/// <summary>
/// One parsed, bounded import row: everything a record means on its own, before anything about this
/// Inventory is known. The Unit and Location are still raw terms here - resolving them needs a store,
/// and this type deliberately needs nothing.
///
/// Every rule it applies is a shipped one: <see cref="NameNormalization"/> for the display and
/// comparison forms, <see cref="Quantity.TryParseInvariant"/> for the amount, and the same length
/// bounds <see cref="StockEntry"/>, <see cref="Unit"/>, and <see cref="Location"/> already enforce -
/// so a file cannot describe stock the conversation could not.
/// </summary>
public sealed record ImportRow
{
    /// <summary>The 1-based source line this row came from, so an error can be found in the file.</summary>
    public required int LineNumber { get; init; }

    public required string Name { get; init; }

    public required string NormalizedName { get; init; }

    public required Quantity Quantity { get; init; }

    /// <summary>The raw Unit term to resolve. Never blank: a blank Unit column means the reserved <c>each</c> Unit.</summary>
    public required string UnitTerm { get; init; }

    /// <summary>The raw Location name to resolve, or null for unlocated - which is the absence of a reference, not a name.</summary>
    public string? LocationName { get; init; }

    public string? Note { get; init; }

    /// <summary>
    /// Reads one record. Every problem with the row is collected rather than the first returned, so a
    /// Participant fixing a file sees everything wrong with a line at once.
    ///
    /// <see cref="CsvImportDocument"/> only ever hands out records with exactly
    /// <see cref="ImportContract.Headers"/>.Count fields, so a caller going through it never sees the
    /// guard below fire. It exists because <see cref="CsvImportRecord"/> is a public record a caller
    /// could construct directly - and indexing a short field list would be an out-of-range crash
    /// rather than a domain refusal.
    /// </summary>
    public static bool TryCreate(CsvImportRecord record, out ImportRow? row, out IReadOnlyList<ImportRowError> errors)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.Fields.Count != ImportContract.Headers.Count)
        {
            throw new ArgumentException(
                $"A record must carry exactly {ImportContract.Headers.Count} fields.",
                nameof(record));
        }

        row = null;
        var found = new List<ImportRowError>();

        var name = Collapsed(record.Fields[ImportContract.NameColumn]);
        var quantityText = record.Fields[ImportContract.QuantityColumn].Trim();
        var unit = Collapsed(record.Fields[ImportContract.UnitColumn]);
        var location = Collapsed(record.Fields[ImportContract.LocationColumn]);
        var note = record.Fields[ImportContract.NoteColumn].Trim();

        if (name.Length == 0)
        {
            found.Add(Error(ImportErrorCode.MissingName, record, ImportContract.NameColumn));
        }
        else if (name.Length > StockEntry.MaxNameLength)
        {
            found.Add(Error(ImportErrorCode.NameTooLong, record, ImportContract.NameColumn));
        }

        var quantity = Quantity.Zero;
        if (quantityText.Length == 0)
        {
            found.Add(Error(ImportErrorCode.MissingQuantity, record, ImportContract.QuantityColumn));
        }
        else if (!Quantity.TryParseInvariant(quantityText, out quantity))
        {
            found.Add(Error(ImportErrorCode.InvalidQuantity, record, ImportContract.QuantityColumn));
        }

        if (unit.Length > Unit.MaxNameLength)
        {
            found.Add(Error(ImportErrorCode.UnitTooLong, record, ImportContract.UnitColumn));
        }

        if (location.Length > Location.MaxNameLength)
        {
            found.Add(Error(ImportErrorCode.LocationTooLong, record, ImportContract.LocationColumn));
        }

        if (note.Length > StockEntry.MaxNoteLength)
        {
            found.Add(Error(ImportErrorCode.NoteTooLong, record, ImportContract.NoteColumn));
        }

        if (found.Count > 0)
        {
            errors = found;
            return false;
        }

        errors = [];
        row = new ImportRow
        {
            LineNumber = record.LineNumber,
            Name = name,
            NormalizedName = NameNormalization.Normalize(name),
            Quantity = quantity,

            // A blank Unit is not a missing Unit: the specification says it means `each`, and saying so
            // here means nothing downstream has to remember it.
            UnitTerm = unit.Length == 0 ? Unit.ReservedEachCanonicalName : unit,
            LocationName = location.Length == 0 ? null : location,
            Note = note.Length == 0 ? null : note,
        };

        return true;
    }

    private static string Collapsed(string value) => NameNormalization.Collapse(value);

    private static ImportRowError Error(ImportErrorCode code, CsvImportRecord record, int columnIndex) =>
        new(code, record.LineNumber, columnIndex);
}
