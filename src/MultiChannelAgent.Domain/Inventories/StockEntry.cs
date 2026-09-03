namespace MultiChannelAgent.Domain.Inventories;

/// <summary>
/// Strongly typed Stock Entry identity, stable for the life of the entry even across Rename/Move
/// merges of Equivalent Stock.
/// </summary>
public readonly record struct StockEntryId(Guid Value)
{
    public override string ToString() => Value.ToString();
}

/// <summary>
/// A stable record of interchangeable units of one kind of thing in an Inventory. References one Unit
/// and optionally one Location; <see cref="Note"/> is free text that never determines Equivalent
/// Stock. Equivalent Stock - the same normalized name, Unit, and optional Location in the same
/// Inventory - is represented by one Stock Entry rather than duplicate entries; <see cref="IsEquivalentTo"/>
/// is the pure predicate persistence's uniqueness constraint must agree with.
/// </summary>
public sealed record StockEntry
{
    /// <summary>
    /// The authoritative maximum length for <see cref="Name"/>, matching the EF Core column's
    /// <c>HasMaxLength</c> configuration so an oversized name is rejected here - as a domain
    /// validation error - long before it could ever reach the database as an unhandled
    /// <see cref="System.Exception"/>.
    /// </summary>
    public const int MaxNameLength = 200;

    /// <summary>The authoritative maximum length for <see cref="Note"/>, for the same reason as <see cref="MaxNameLength"/>.</summary>
    public const int MaxNoteLength = 500;

    public required StockEntryId Id { get; init; }

    public required InventoryId InventoryId { get; init; }

    public required UnitId UnitId { get; init; }

    public LocationId? LocationId { get; init; }

    public required string Name { get; init; }

    public required string NormalizedName { get; init; }

    public string? Note { get; init; }

    public required Quantity Quantity { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public static StockEntry Create(
        InventoryId inventoryId,
        UnitId unitId,
        LocationId? locationId,
        string? name,
        string? note,
        Quantity quantity,
        DateTimeOffset createdAt)
    {
        var trimmedName = RequireWithinBounds(RequireNonBlank(name, nameof(name)), MaxNameLength, nameof(name));
        var trimmedNote = NormalizeOptional(note);
        if (trimmedNote is not null && trimmedNote.Length > MaxNoteLength)
        {
            throw new ArgumentException($"Value must not exceed {MaxNoteLength} characters.", nameof(note));
        }

        return new StockEntry
        {
            Id = new StockEntryId(Guid.NewGuid()),
            InventoryId = inventoryId,
            UnitId = unitId,
            LocationId = locationId,
            Name = trimmedName,
            NormalizedName = NameNormalization.Normalize(trimmedName),
            Note = trimmedNote,
            Quantity = quantity,
            CreatedAt = createdAt,
        };
    }

    /// <summary>
    /// True when <paramref name="other"/> is Equivalent Stock to this entry: the same normalized
    /// name, Unit, and optional Location within the same Inventory. Note is deliberately excluded -
    /// per <c>CONTEXT.md</c> it never determines Equivalent Stock.
    /// </summary>
    public bool IsEquivalentTo(StockEntry other) =>
        InventoryId == other.InventoryId
        && NormalizedName == other.NormalizedName
        && UnitId == other.UnitId
        && LocationId == other.LocationId;

    private static string RequireNonBlank(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value must not be blank.", parameterName);
        }

        return value.Trim();
    }

    private static string RequireWithinBounds(string value, int maxLength, string parameterName)
    {
        if (value.Length > maxLength)
        {
            throw new ArgumentException($"Value must not exceed {maxLength} characters.", parameterName);
        }

        return value;
    }

    private static string? NormalizeOptional(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
