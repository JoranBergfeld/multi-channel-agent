namespace MultiChannelAgent.Domain.Inventories;

/// <summary>
/// Strongly typed Location identity, stable for the life of the Location even if it is later retired
/// (its identity remains valid for prior Stock Entry references and audits).
/// </summary>
public readonly record struct LocationId(Guid Value)
{
    public override string ToString() => Value.ToString();
}

/// <summary>
/// A stable, flat named place used to distinguish where Stock is kept within an Inventory. Its name
/// is unique case-insensitively within that Inventory - <see cref="NormalizedName"/> is the value
/// persistence enforces that uniqueness against, computed the same way every other name comparison in
/// this domain is (<see cref="NameNormalization"/>).
/// </summary>
public sealed record Location
{
    /// <summary>
    /// The authoritative maximum length for <see cref="Name"/>, matching the EF Core column's
    /// <c>HasMaxLength</c> configuration so an oversized name is rejected here - as a domain
    /// validation error - long before it could ever reach the database as an unhandled
    /// <see cref="System.Exception"/>.
    /// </summary>
    public const int MaxNameLength = 200;

    public required LocationId Id { get; init; }

    public required InventoryId InventoryId { get; init; }

    public required string Name { get; init; }

    public required string NormalizedName { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When this Location was withdrawn from matching and assignment, or null while it is active.</summary>
    public DateTimeOffset? RetiredAt { get; init; }

    /// <summary>Active Locations are the only ones that resolve, match, or appear in ordinary Lists. Unlocated stock is the absence of a reference and can never be retired.</summary>
    public bool IsActive => RetiredAt is null;

    public static Location Create(InventoryId inventoryId, string? name, DateTimeOffset createdAt)
    {
        var displayName = RequireWithinBounds(RequireNonBlank(name, nameof(name)), MaxNameLength, nameof(name));

        return new Location
        {
            Id = new LocationId(Guid.NewGuid()),
            InventoryId = inventoryId,
            Name = displayName,
            NormalizedName = NameNormalization.Normalize(displayName),
            CreatedAt = createdAt,
        };
    }

    private static string RequireNonBlank(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value must not be blank.", parameterName);
        }

        return NameNormalization.Collapse(value);
    }

    private static string RequireWithinBounds(string value, int maxLength, string parameterName)
    {
        if (value.Length > maxLength)
        {
            throw new ArgumentException($"Value must not exceed {maxLength} characters.", parameterName);
        }

        return value;
    }
}
