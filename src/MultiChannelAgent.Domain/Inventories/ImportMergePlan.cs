namespace MultiChannelAgent.Domain.Inventories;

/// <summary>
/// One import row whose Unit and Location have been resolved to identities. The display names come
/// along so the preview can show what the Inventory actually calls them rather than what the file
/// happened to type.
/// </summary>
public sealed record ResolvedImportRow
{
    public required int LineNumber { get; init; }

    public required string Name { get; init; }

    public required string NormalizedName { get; init; }

    public required Quantity Quantity { get; init; }

    public required UnitId UnitId { get; init; }

    public required string UnitCanonicalName { get; init; }

    public LocationId? LocationId { get; init; }

    public string? LocationName { get; init; }

    public string? Note { get; init; }
}

/// <summary>
/// One Stock Entry the import will create, and every source line that contributed to it - so a
/// Participant reviewing the preview can see exactly which rows collapsed into it.
/// </summary>
public sealed record ImportEntry
{
    /// <summary>The first source line of the group, which is also the line whose display text and references survive.</summary>
    public required int LineNumber { get; init; }

    public required IReadOnlyList<int> SourceLineNumbers { get; init; }

    public required string Name { get; init; }

    public required string NormalizedName { get; init; }

    public required Quantity Quantity { get; init; }

    public required UnitId UnitId { get; init; }

    public required string UnitCanonicalName { get; init; }

    public LocationId? LocationId { get; init; }

    public string? LocationName { get; init; }

    public string? Note { get; init; }
}

/// <summary>The merged result, or the reasons it could not be merged. Both are never partly true: entries are empty whenever anything failed.</summary>
public sealed record ImportMergeResult(IReadOnlyList<ImportEntry> Entries, IReadOnlyList<ImportRowError> Errors);

/// <summary>
/// The pure merge. Rows are equivalent exactly when the domain says they are - same normalized name,
/// same Unit, same optional Location, which is the key the database's Equivalent Stock index enforces
/// - and Notes deliberately do not participate, as <c>CONTEXT.md</c> states.
///
/// Notes are compared ordinally and case-sensitively after trimming. A Note is free text somebody
/// wrote to record a distinction, so folding "Blue box" into "blue box" would quietly erase one;
/// refusing and asking is the safe direction, and it is the direction a Participant can act on.
/// </summary>
public static class ImportMergePlan
{
    public static ImportMergeResult Create(IReadOnlyList<ResolvedImportRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var groups = new Dictionary<EquivalenceKey, List<ResolvedImportRow>>();
        var order = new List<EquivalenceKey>();

        // Rows are trusted to already be in source line order; a group's position in the result is
        // simply the position its first row was encountered, so no re-sort happens here.
        foreach (var row in rows)
        {
            var key = new EquivalenceKey(row.NormalizedName, row.UnitId, row.LocationId);

            if (!groups.TryGetValue(key, out var group))
            {
                group = [];
                groups[key] = group;
                order.Add(key);
            }

            group.Add(row);
        }

        var errors = new List<ImportRowError>();
        var entries = new List<ImportEntry>(order.Count);

        foreach (var key in order)
        {
            var group = groups[key];
            var first = group[0];

            if (!TryMergeNotes(group, errors, out var note))
            {
                continue;
            }

            if (!TrySum(group, out var quantity))
            {
                errors.Add(new ImportRowError(ImportErrorCode.QuantityOverflow, first.LineNumber, ImportContract.QuantityColumn));
                continue;
            }

            entries.Add(new ImportEntry
            {
                LineNumber = first.LineNumber,
                SourceLineNumbers = [.. group.Select(row => row.LineNumber).OrderBy(lineNumber => lineNumber)],
                Name = first.Name,
                NormalizedName = first.NormalizedName,
                Quantity = quantity,
                UnitId = first.UnitId,
                UnitCanonicalName = first.UnitCanonicalName,
                LocationId = first.LocationId,
                LocationName = first.LocationName,
                Note = note,
            });
        }

        if (errors.Count > 0)
        {
            return new ImportMergeResult([], errors);
        }

        // Checked after merging, because the bound is on the Stock Entries this would create, not on
        // the rows that describe them - a file may legitimately carry more rows than entries.
        return entries.Count > ImportContract.MaxNormalizedEntries
            ? new ImportMergeResult([], [new ImportRowError(ImportErrorCode.TooManyEntries, 0, null)])
            : new ImportMergeResult(entries, []);
    }

    /// <summary>
    /// Decides the group's surviving Note. Blanks are compatible with anything and contribute nothing;
    /// one distinct non-blank Note survives; two are a conflict reported against every line after the
    /// first that introduced a different one, so one pass over the file fixes it.
    /// </summary>
    private static bool TryMergeNotes(List<ResolvedImportRow> group, List<ImportRowError> errors, out string? note)
    {
        note = null;
        var conflicted = false;

        foreach (var row in group)
        {
            if (row.Note is null)
            {
                continue;
            }

            if (note is null)
            {
                note = row.Note;
                continue;
            }

            if (!string.Equals(note, row.Note, StringComparison.Ordinal))
            {
                errors.Add(new ImportRowError(ImportErrorCode.ConflictingNotes, row.LineNumber, ImportContract.NoteColumn));
                conflicted = true;
            }
        }

        return !conflicted;
    }

    private static bool TrySum(List<ResolvedImportRow> group, out Quantity total)
    {
        total = Quantity.Zero;

        foreach (var row in group)
        {
            if (!total.TryAdd(row.Quantity, out var next))
            {
                return false;
            }

            total = next;
        }

        return true;
    }

    private readonly record struct EquivalenceKey(string NormalizedName, UnitId UnitId, LocationId? LocationId);
}
