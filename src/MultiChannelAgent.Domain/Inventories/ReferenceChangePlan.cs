namespace MultiChannelAgent.Domain.Inventories;

/// <summary>Whether one administration change could be decided at all, and if not, why not.</summary>
public enum ReferenceChangePlanOutcome
{
    /// <summary>Decided exactly, and ready to be applied or proposed.</summary>
    Planned,

    /// <summary>A name or alias was blank, or longer than it can be stored.</summary>
    InvalidName,

    /// <summary>The term already identifies an active Unit in this Inventory - possibly this very one.</summary>
    TermInUse,

    /// <summary>An active Location in this Inventory already carries that name.</summary>
    NameInUse,

    /// <summary>The change would leave the Inventory exactly as it is, so it is a semantic no-op rather than work.</summary>
    NoChange,

    /// <summary>The reserved `each` Unit can never be renamed or retired.</summary>
    ReservedUnit,

    /// <summary>A fixed alias of the reserved `each` Unit can never be removed.</summary>
    ReservedTerm,

    /// <summary>A Unit's own canonical name is not one of its aliases, so it cannot be removed as one.</summary>
    CanonicalTerm,

    /// <summary>That term is not an active alias of that Unit.</summary>
    AliasNotFound,

    /// <summary>A Stock Entry still references it, so retiring it would rewrite stock - which administration never does.</summary>
    ReferenceInUse,
}

/// <summary>
/// The pure decision one administration change amounts to, given only current state: what the
/// Inventory's active terms and Location names are, which terms the target Unit carries, whether it
/// is the reserved one, and how many Stock Entries reference it. It reads and writes nothing -
/// authorization, resolution, proposals, and persistence all live outside it - so every rule about
/// the shared namespace, the reserved Unit, no-ops, and retire-blocking can be reasoned about, and
/// tested, on its own.
///
/// Nothing here is fuzzy: comparisons are on the normalized form and are exact.
/// </summary>
public sealed record ReferenceChangePlan
{
    public required ReferenceChangePlanOutcome Outcome { get; init; }

    /// <summary>The tidied display name a create or rename establishes; empty for every other kind and every refusal.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>The normalized form of <see cref="DisplayName"/>, computed once here so no executor re-normalizes.</summary>
    public string NormalizedName { get; init; } = string.Empty;

    /// <summary>The full ordered term set a Unit creation establishes - canonical first, then aliases. Empty for every other kind.</summary>
    public IReadOnlyList<UnitTerm> Terms { get; init; } = [];

    /// <summary>The single term an alias add establishes or an alias removal ends; null for every other kind.</summary>
    public UnitTerm? Term { get; init; }

    /// <summary>
    /// Plans creating a Unit. <paramref name="activeNormalizedTerms"/> is every normalized term that
    /// currently identifies an active Unit anywhere in this Inventory.
    /// </summary>
    public static ReferenceChangePlan ForCreateUnit(
        string? canonicalName, IReadOnlyList<string> aliases, IReadOnlySet<string> activeNormalizedTerms)
    {
        ArgumentNullException.ThrowIfNull(aliases);
        ArgumentNullException.ThrowIfNull(activeNormalizedTerms);

        if (!TryTidy(canonicalName, Unit.MaxNameLength, out var name))
        {
            return Refused(ReferenceChangePlanOutcome.InvalidName);
        }

        var terms = new List<UnitTerm> { UnitTerm.Create(name, isCanonical: true, isReserved: false) };

        foreach (var alias in aliases)
        {
            if (!TryTidy(alias, Unit.MaxNameLength, out var tidyAlias))
            {
                return Refused(ReferenceChangePlanOutcome.InvalidName);
            }

            terms.Add(UnitTerm.Create(tidyAlias, isCanonical: false, isReserved: false));
        }

        // Two collisions matter and they are the same failure: a term already identifying an active
        // Unit, and one this very creation would claim twice. Left to the database both are a unique
        // index violation halfway through a transaction; refused here, both are one plain answer.
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var term in terms)
        {
            if (activeNormalizedTerms.Contains(term.NormalizedTerm) || !claimed.Add(term.NormalizedTerm))
            {
                return Refused(ReferenceChangePlanOutcome.TermInUse);
            }
        }

        return new ReferenceChangePlan
        {
            Outcome = ReferenceChangePlanOutcome.Planned,
            DisplayName = name,
            NormalizedName = terms[0].NormalizedTerm,
            Terms = terms,
        };
    }

    /// <summary>
    /// Plans renaming a Unit. <paramref name="otherActiveNormalizedTerms"/> is every normalized term
    /// identifying an active Unit here <em>except</em> this Unit's own canonical term - its own
    /// aliases are included, because promoting an alias to canonical would be a reference merge, and
    /// merging is out of scope.
    /// </summary>
    public static ReferenceChangePlan ForRenameUnit(
        bool isReserved,
        string currentDisplayName,
        string currentNormalizedName,
        string? newName,
        IReadOnlySet<string> otherActiveNormalizedTerms)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDisplayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentNormalizedName);
        ArgumentNullException.ThrowIfNull(otherActiveNormalizedTerms);

        if (isReserved)
        {
            return Refused(ReferenceChangePlanOutcome.ReservedUnit);
        }

        if (!TryTidy(newName, Unit.MaxNameLength, out var name))
        {
            return Refused(ReferenceChangePlanOutcome.InvalidName);
        }

        return Rename(currentDisplayName, currentNormalizedName, name, otherActiveNormalizedTerms, ReferenceChangePlanOutcome.TermInUse);
    }

    /// <summary>
    /// Plans adding one alias. <paramref name="unitTerms"/> is what the target Unit already carries;
    /// <paramref name="otherActiveNormalizedTerms"/> is every active term belonging to a
    /// <em>different</em> Unit here.
    /// </summary>
    public static ReferenceChangePlan ForAddUnitAlias(
        string? alias, IReadOnlyList<UnitTerm> unitTerms, IReadOnlySet<string> otherActiveNormalizedTerms)
    {
        ArgumentNullException.ThrowIfNull(unitTerms);
        ArgumentNullException.ThrowIfNull(otherActiveNormalizedTerms);

        if (!TryTidy(alias, Unit.MaxNameLength, out var tidyAlias))
        {
            return Refused(ReferenceChangePlanOutcome.InvalidName);
        }

        var normalized = NameNormalization.Normalize(tidyAlias);

        // A term the Unit already answers to - canonical or alias - would change nothing at all.
        if (unitTerms.Any(term => term.NormalizedTerm == normalized))
        {
            return Refused(ReferenceChangePlanOutcome.NoChange);
        }

        // A term identifying another active Unit cannot be taken. This is also exactly why a reserved
        // term can never be reassigned: `each`, `piece`, `pieces`, `pc`, and `pcs` are always active
        // terms of the reserved Unit, so they are always in this set for every other Unit.
        if (otherActiveNormalizedTerms.Contains(normalized))
        {
            return Refused(ReferenceChangePlanOutcome.TermInUse);
        }

        return new ReferenceChangePlan
        {
            Outcome = ReferenceChangePlanOutcome.Planned,
            Term = UnitTerm.Create(tidyAlias, isCanonical: false, isReserved: false),
        };
    }

    /// <summary>Plans removing one alias from the terms the target Unit carries.</summary>
    public static ReferenceChangePlan ForRemoveUnitAlias(string? alias, IReadOnlyList<UnitTerm> unitTerms)
    {
        ArgumentNullException.ThrowIfNull(unitTerms);

        if (!TryTidy(alias, Unit.MaxNameLength, out var tidyAlias))
        {
            return Refused(ReferenceChangePlanOutcome.InvalidName);
        }

        var normalized = NameNormalization.Normalize(tidyAlias);
        var existing = unitTerms.FirstOrDefault(term => term.NormalizedTerm == normalized);

        return existing switch
        {
            null => Refused(ReferenceChangePlanOutcome.AliasNotFound),
            { IsCanonical: true } => Refused(ReferenceChangePlanOutcome.CanonicalTerm),
            { IsReserved: true } => Refused(ReferenceChangePlanOutcome.ReservedTerm),
            _ => new ReferenceChangePlan { Outcome = ReferenceChangePlanOutcome.Planned, Term = existing },
        };
    }

    /// <summary>Plans retiring a Unit. Retire withdraws an <em>unused</em> reference; it never rewrites stock.</summary>
    public static ReferenceChangePlan ForRetireUnit(bool isReserved, int stockReferenceCount)
    {
        if (isReserved)
        {
            return Refused(ReferenceChangePlanOutcome.ReservedUnit);
        }

        return stockReferenceCount > 0
            ? Refused(ReferenceChangePlanOutcome.ReferenceInUse)
            : new ReferenceChangePlan { Outcome = ReferenceChangePlanOutcome.Planned };
    }

    /// <summary>Plans creating a Location. <paramref name="activeNormalizedNames"/> is every active Location name here.</summary>
    public static ReferenceChangePlan ForCreateLocation(string? name, IReadOnlySet<string> activeNormalizedNames)
    {
        ArgumentNullException.ThrowIfNull(activeNormalizedNames);

        if (!TryTidy(name, Location.MaxNameLength, out var displayName))
        {
            return Refused(ReferenceChangePlanOutcome.InvalidName);
        }

        var normalized = NameNormalization.Normalize(displayName);

        return activeNormalizedNames.Contains(normalized)
            ? Refused(ReferenceChangePlanOutcome.NameInUse)
            : new ReferenceChangePlan
            {
                Outcome = ReferenceChangePlanOutcome.Planned,
                DisplayName = displayName,
                NormalizedName = normalized,
            };
    }

    /// <summary>
    /// Plans renaming a Location. <paramref name="otherActiveNormalizedNames"/> is every active
    /// Location name here except this Location's own.
    /// </summary>
    public static ReferenceChangePlan ForRenameLocation(
        string currentDisplayName,
        string currentNormalizedName,
        string? newName,
        IReadOnlySet<string> otherActiveNormalizedNames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDisplayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentNormalizedName);
        ArgumentNullException.ThrowIfNull(otherActiveNormalizedNames);

        if (!TryTidy(newName, Location.MaxNameLength, out var displayName))
        {
            return Refused(ReferenceChangePlanOutcome.InvalidName);
        }

        return Rename(
            currentDisplayName, currentNormalizedName, displayName, otherActiveNormalizedNames, ReferenceChangePlanOutcome.NameInUse);
    }

    /// <summary>Plans retiring a Location. Unlocated stock is the absence of a reference, so it is never a target here.</summary>
    public static ReferenceChangePlan ForRetireLocation(int stockReferenceCount) => stockReferenceCount > 0
        ? Refused(ReferenceChangePlanOutcome.ReferenceInUse)
        : new ReferenceChangePlan { Outcome = ReferenceChangePlanOutcome.Planned };

    /// <summary>
    /// The rename decision both kinds share. Only case and whitespace are normalized away, so a new
    /// name whose normalized form is unchanged can collide with nothing but the reference itself: the
    /// displayed name changes and the identity is untouched.
    /// </summary>
    private static ReferenceChangePlan Rename(
        string currentDisplayName,
        string currentNormalizedName,
        string newDisplayName,
        IReadOnlySet<string> takenNormalizedNames,
        ReferenceChangePlanOutcome collisionOutcome)
    {
        if (string.Equals(currentDisplayName, newDisplayName, StringComparison.Ordinal))
        {
            return Refused(ReferenceChangePlanOutcome.NoChange);
        }

        var normalized = NameNormalization.Normalize(newDisplayName);

        if (normalized != currentNormalizedName && takenNormalizedNames.Contains(normalized))
        {
            return Refused(collisionOutcome);
        }

        return new ReferenceChangePlan
        {
            Outcome = ReferenceChangePlanOutcome.Planned,
            DisplayName = newDisplayName,
            NormalizedName = normalized,
        };
    }

    /// <summary>
    /// Tidies an untrusted name into the display form that will be stored, or refuses it. Case is
    /// deliberately left exactly as written: <see cref="NameNormalization.Normalize"/> is what
    /// comparison uses, and folding case here would store a name nobody asked for.
    /// </summary>
    private static bool TryTidy(string? value, int maxLength, out string tidy)
    {
        tidy = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var collapsed = NameNormalization.Collapse(value);
        if (collapsed.Length == 0 || collapsed.Length > maxLength)
        {
            return false;
        }

        tidy = collapsed;
        return true;
    }

    private static ReferenceChangePlan Refused(ReferenceChangePlanOutcome outcome) => new() { Outcome = outcome };
}
