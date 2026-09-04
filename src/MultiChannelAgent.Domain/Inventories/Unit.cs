namespace MultiChannelAgent.Domain.Inventories;

/// <summary>
/// Strongly typed Unit identity, stable for the life of the Unit even if its canonical name or
/// aliases change later (needed now so a future Unit-administration ticket can rename without
/// rewriting Stock Entry references).
/// </summary>
public readonly record struct UnitId(Guid Value)
{
    public override string ToString() => Value.ToString();
}

/// <summary>
/// One term in a Unit's shared, collision-free namespace: its canonical name, or one alias. The
/// normalized form is what uniqueness is enforced against, computed the same way every other name
/// comparison in this domain is (<see cref="NameNormalization"/>).
///
/// <see cref="IsReserved"/> is per-term rather than derived from the Unit, so the reserved `each`
/// Unit's five fixed terms can be protected while an alias a Participant later teaches it stays
/// removable.
/// </summary>
public sealed record UnitTerm
{
    public required string Term { get; init; }

    public required string NormalizedTerm { get; init; }

    public required bool IsCanonical { get; init; }

    public required bool IsReserved { get; init; }

    public static UnitTerm Create(string? term, bool isCanonical, bool isReserved)
    {
        var trimmed = Unit.RequireTermWithinBounds(term, nameof(term));

        return new UnitTerm
        {
            Term = trimmed,
            NormalizedTerm = NameNormalization.Normalize(trimmed),
            IsCanonical = isCanonical,
            IsReserved = isReserved,
        };
    }
}

/// <summary>
/// An Inventory-owned controlled measure. Every Inventory starts with exactly one reserved Unit:
/// the canonical `each`, with the fixed aliases `piece`, `pieces`, `pc`, and `pcs`. Unit names and
/// aliases share one collision-free namespace within an Inventory.
///
/// <see cref="Id"/> is stable for the life of the Unit: renaming changes only what it is called, and
/// retiring withdraws it from matching and assignment without ending the identity that prior Stock
/// Entry references and audits depend on.
/// </summary>
public sealed record Unit
{
    /// <summary>
    /// The authoritative maximum length for a canonical name and for every alias, matching the EF
    /// Core columns' <c>HasMaxLength</c> configuration so an oversized term is rejected here - as a
    /// domain validation error - long before it could reach the database as an unhandled exception.
    /// </summary>
    public const int MaxNameLength = 100;

    /// <summary>The canonical name of the reserved Unit every Inventory starts with.</summary>
    public const string ReservedEachCanonicalName = "each";

    public static readonly IReadOnlyList<string> ReservedEachAliases = ["piece", "pieces", "pc", "pcs"];

    public required UnitId Id { get; init; }

    public required InventoryId InventoryId { get; init; }

    public required string CanonicalName { get; init; }

    public required bool IsReserved { get; init; }

    public required IReadOnlyList<string> Aliases { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When this Unit was withdrawn from matching and assignment, or null while it is active.</summary>
    public DateTimeOffset? RetiredAt { get; init; }

    /// <summary>Active Units are the only ones that resolve, match, or appear in ordinary Lists.</summary>
    public bool IsActive => RetiredAt is null;

    public static Unit CreateReservedEach(InventoryId inventoryId, DateTimeOffset createdAt) => new()
    {
        Id = new UnitId(Guid.NewGuid()),
        InventoryId = inventoryId,
        CanonicalName = ReservedEachCanonicalName,
        IsReserved = true,
        Aliases = ReservedEachAliases,
        CreatedAt = createdAt,
    };

    /// <summary>
    /// Creates a non-reserved Unit with a canonical name and an ordered set of initial aliases. The
    /// aliases are part of creating the Unit - exactly as the reserved `each` Unit is created with
    /// its four - so a Unit's whole term set is established atomically and every later alias change
    /// is one auditable fact of its own.
    ///
    /// Collision against other Units is not decided here: this type knows nothing about the rest of
    /// the Inventory. It only refuses what is malformed on its own terms.
    /// </summary>
    public static Unit Create(
        InventoryId inventoryId, string? canonicalName, IReadOnlyList<string> aliases, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(aliases);

        return new Unit
        {
            Id = new UnitId(Guid.NewGuid()),
            InventoryId = inventoryId,
            CanonicalName = RequireTermWithinBounds(canonicalName, nameof(canonicalName)),
            IsReserved = false,
            Aliases = aliases.Select(alias => RequireTermWithinBounds(alias, nameof(aliases))).ToList(),
            CreatedAt = createdAt,
        };
    }

    /// <summary>
    /// The full term set this Unit contributes to its Inventory's shared namespace: its canonical
    /// name first, then its aliases in the order they were given.
    /// </summary>
    public IReadOnlyList<UnitTerm> Terms() =>
    [
        UnitTerm.Create(CanonicalName, isCanonical: true, isReserved: IsReserved),
        .. Aliases.Select(alias => UnitTerm.Create(alias, isCanonical: false, isReserved: IsReserved)),
    ];

    /// <summary>
    /// Whether a term is one of the five the reserved `each` Unit is born with. Compared on the
    /// normalized form, so casing and stray whitespace cannot smuggle one past.
    /// </summary>
    public static bool IsReservedEachTerm(string? term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return false;
        }

        var normalized = NameNormalization.Normalize(term);

        return normalized == ReservedEachCanonicalName
            || ReservedEachAliases.Any(alias => alias == normalized);
    }

    internal static string RequireTermWithinBounds(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value must not be blank.", parameterName);
        }

        var trimmed = NameNormalization.Collapse(value);

        if (trimmed.Length > MaxNameLength)
        {
            throw new ArgumentException($"Value must not exceed {MaxNameLength} characters.", parameterName);
        }

        return trimmed;
    }
}
