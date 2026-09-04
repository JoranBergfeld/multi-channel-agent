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

/// <summary>
/// Every row whose Unit and Location resolved, and the errors for those that did not. <see cref="Rows"/>
/// carries every successfully resolved row even when other rows in the same call have reference
/// errors - a row's own resolution never depends on any other row's - so a caller can still act on
/// what did resolve (merge it, report merge errors alongside these) rather than deferring all of it to
/// a second upload just because some other line named an unknown Unit.
/// </summary>
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
///
/// Suggestions are bounded separately from resolution itself: <paramref name="suggestionBudget" />
/// (see <see cref="ResolveAsync"/>) caps how many distinct unknown terms may ever query
/// <see cref="IReferenceCatalogStore.SuggestAsync"/> in one call, because a caller can only ever act on
/// <see cref="ImportContract.MaxReportedErrors"/> of them anyway. Identity resolution is never bounded
/// by this budget - every row is still resolved and every unknown reference still becomes an exact
/// error - only the catalog round trip behind its suggestions is skipped once the budget is spent, and
/// such an error simply carries no suggestions.
/// </summary>
public sealed class ImportReferenceResolver(IInventoryReferenceStore references, IReferenceCatalogStore catalog)
{
    /// <param name="suggestionBudget">
    /// How many distinct unknown terms may query <see cref="IReferenceCatalogStore.SuggestAsync"/> in
    /// this call. A repeated unknown term never spends the budget twice - it is served from the same
    /// per-call cache resolution itself uses. Zero means no catalog calls at all; every unknown
    /// reference is still reported, just with an empty suggestion list. Must not be negative.
    /// </param>
    public async Task<ImportResolutionResult> ResolveAsync(
        InventoryId inventoryId, IReadOnlyList<ImportRow> rows, int suggestionBudget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentOutOfRangeException.ThrowIfNegative(suggestionBudget);
        cancellationToken.ThrowIfCancellationRequested();

        var units = new Dictionary<string, (UnitId Id, string CanonicalName)?>(StringComparer.Ordinal);
        var locations = new Dictionary<string, (LocationId Id, string Name)?>(StringComparer.Ordinal);
        var suggestions = new Dictionary<(ReferenceKind Kind, string Normalized), IReadOnlyList<string>>();
        var budget = new SuggestionBudget(suggestionBudget);

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
                    row.LineNumber, ImportContract.UnitColumn, suggestions, budget, cancellationToken));
            }

            if (locationUnknown)
            {
                errors.Add(await UnknownAsync(
                    inventoryId, ReferenceKind.Location, row.LocationName!, ImportErrorCode.UnknownLocation,
                    row.LineNumber, ImportContract.LocationColumn, suggestions, budget, cancellationToken));
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

        return new ImportResolutionResult(resolved, errors);
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
        SuggestionBudget budget,
        CancellationToken cancellationToken)
    {
        var key = (kind, NameNormalization.Normalize(reference));
        if (!cache.TryGetValue(key, out var suggestions))
        {
            // Only ever reached after the caller has been authorized for this Inventory, and only
            // ever naming references the caller could list anyway, so suggestions disclose nothing
            // new. Cached alongside resolution, for the same reason: one unknown term repeated across
            // a file should cost one suggestion lookup, not one per row.
            //
            // The budget is spent here, on a cache miss, and only here: a term already in the cache -
            // whether it queried the catalog or not - never spends it again. Once the budget is gone,
            // resolution keeps going (a five-thousand-row file still gets an exact error per unknown
            // row) but no further term ever reaches the catalog; it is cached as having no suggestions
            // so a second occurrence still costs nothing.
            suggestions = budget.TryConsume()
                ? await catalog.SuggestAsync(inventoryId, kind, reference, cancellationToken)
                : [];
            cache[key] = suggestions;
        }

        return new ImportReferenceError(new ImportRowError(code, lineNumber, columnIndex), suggestions);
    }

    /// <summary>
    /// How many more distinct unknown terms may query the catalog in one <see cref="ResolveAsync"/>
    /// call. A mutable per-call counter rather than a plain int, because <see cref="UnknownAsync"/> is
    /// its own method rather than a closure and an async method cannot take a <see langword="ref"/>
    /// parameter.
    /// </summary>
    private sealed class SuggestionBudget(int remaining)
    {
        public bool TryConsume()
        {
            if (remaining <= 0)
            {
                return false;
            }

            remaining--;
            return true;
        }
    }
}
