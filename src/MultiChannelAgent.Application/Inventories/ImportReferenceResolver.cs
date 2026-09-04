using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>
/// One import error, plus the bounded suggestions an unknown reference offers. Suggestions are empty
/// for every error that is not about a reference.
/// </summary>
public sealed record ImportReferenceError(ImportRowError Error, IReadOnlyList<string> Suggestions)
{
    public ImportErrorCode Code => Error.Code;

    public int LineNumber => Error.LineNumber;

    public int? ColumnIndex => Error.ColumnIndex;
}

/// <summary>The rows whose references resolved, and the errors for those that did not. Rows are empty whenever anything failed.</summary>
public sealed record ImportResolutionResult(IReadOnlyList<ResolvedImportRow> Rows, IReadOnlyList<ImportReferenceError> Errors);

/// <summary>
/// Resolves every row's Unit term and Location name to identities, using the shipped active-only
/// <see cref="IInventoryReferenceStore"/>, and reports the ones that do not resolve.
///
/// Nothing is ever created. #26 is explicit - "unknown Units and Locations reported instead of created
/// implicitly" - and creating one here would be an unreviewed reference-administration act by a
/// workflow nobody asked to administer references.
///
/// Each distinct term is resolved once and cached for the life of one validation, so a five-thousand
/// row file with three Units performs three lookups - and a negative result is cached too, so a
/// five-thousand row file naming one unknown Unit performs one lookup, not five thousand. The cache
/// key is <see cref="NameNormalization.Normalize"/>, the same fold the underlying store itself
/// resolves by, so "each", " each ", and "EACH" share one entry rather than three: a raw
/// case-insensitive key would still miss whenever rows disagree only on internal whitespace. The
/// cache never outlives the call, so it can never serve a reference that was retired since.
/// </summary>
public sealed class ImportReferenceResolver(IInventoryReferenceStore references, IReferenceCatalogStore catalog)
{
    public async Task<ImportResolutionResult> ResolveAsync(
        InventoryId inventoryId, IReadOnlyList<ImportRow> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        cancellationToken.ThrowIfCancellationRequested();

        var units = new Dictionary<string, (UnitId Id, string CanonicalName)?>(StringComparer.Ordinal);
        var locations = new Dictionary<string, (LocationId Id, string Name)?>(StringComparer.Ordinal);
        var suggestions = new Dictionary<(ReferenceKind Kind, string Normalized), IReadOnlyList<string>>();

        var resolved = new List<ResolvedImportRow>(rows.Count);
        var errors = new List<ImportReferenceError>();

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var unit = await ResolveUnitAsync(inventoryId, row.UnitTerm, units, cancellationToken);

            // Location is always checked when the file names one, whatever the Unit's outcome: a row
            // with both an unknown Unit and an unknown Location must report both, so one pass over the
            // file fixes it rather than the Unit first and the Location on a second attempt.
            (LocationId Id, string Name)? location = null;
            var locationUnknown = false;
            if (row.LocationName is { } locationName)
            {
                location = await ResolveLocationAsync(inventoryId, locationName, locations, cancellationToken);
                locationUnknown = location is null;
            }

            if (unit is null)
            {
                errors.Add(await UnknownAsync(
                    inventoryId, ReferenceKind.Unit, row.UnitTerm, ImportErrorCode.UnknownUnit,
                    row.LineNumber, ImportContract.UnitColumn, suggestions, cancellationToken));
            }

            if (locationUnknown)
            {
                errors.Add(await UnknownAsync(
                    inventoryId, ReferenceKind.Location, row.LocationName!, ImportErrorCode.UnknownLocation,
                    row.LineNumber, ImportContract.LocationColumn, suggestions, cancellationToken));
            }

            if (unit is null || locationUnknown)
            {
                continue;
            }

            resolved.Add(new ResolvedImportRow
            {
                LineNumber = row.LineNumber,
                Name = row.Name,
                NormalizedName = row.NormalizedName,
                Quantity = row.Quantity,
                UnitId = unit.Value.Id,
                UnitCanonicalName = unit.Value.CanonicalName,
                LocationId = location?.Id,
                LocationName = location?.Name,
                Note = row.Note,
            });
        }

        return errors.Count > 0
            ? new ImportResolutionResult([], errors)
            : new ImportResolutionResult(resolved, []);
    }

    private async Task<(UnitId Id, string CanonicalName)?> ResolveUnitAsync(
        InventoryId inventoryId,
        string term,
        Dictionary<string, (UnitId Id, string CanonicalName)?> cache,
        CancellationToken cancellationToken)
    {
        var key = NameNormalization.Normalize(term);
        if (cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        (UnitId Id, string CanonicalName)? resolved = null;

        if (await references.ResolveUnitAsync(inventoryId, term, cancellationToken) is { } unitId
            && await references.FindUnitCanonicalNameAsync(inventoryId, unitId, cancellationToken) is { } canonicalName)
        {
            resolved = (unitId, canonicalName);
        }

        cache[key] = resolved;
        return resolved;
    }

    private async Task<(LocationId Id, string Name)?> ResolveLocationAsync(
        InventoryId inventoryId,
        string name,
        Dictionary<string, (LocationId Id, string Name)?> cache,
        CancellationToken cancellationToken)
    {
        var key = NameNormalization.Normalize(name);
        if (cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        (LocationId Id, string Name)? resolved = null;

        if (await references.ResolveLocationAsync(inventoryId, name, cancellationToken) is { } locationId
            && await references.FindLocationNameAsync(inventoryId, locationId, cancellationToken) is { } displayName)
        {
            resolved = (locationId, displayName);
        }

        cache[key] = resolved;
        return resolved;
    }

    private async Task<ImportReferenceError> UnknownAsync(
        InventoryId inventoryId,
        ReferenceKind kind,
        string reference,
        ImportErrorCode code,
        int lineNumber,
        int columnIndex,
        Dictionary<(ReferenceKind Kind, string Normalized), IReadOnlyList<string>> cache,
        CancellationToken cancellationToken)
    {
        var key = (kind, NameNormalization.Normalize(reference));
        if (!cache.TryGetValue(key, out var suggestions))
        {
            // Only ever reached after the caller has been authorized for this Inventory, and only
            // ever naming references the caller could list anyway, so suggestions disclose nothing
            // new. Cached alongside resolution, for the same reason: one unknown term repeated across
            // a file should cost one suggestion lookup, not one per row.
            suggestions = await catalog.SuggestAsync(inventoryId, kind, reference, cancellationToken);
            cache[key] = suggestions;
        }

        return new ImportReferenceError(new ImportRowError(code, lineNumber, columnIndex), suggestions);
    }
}
