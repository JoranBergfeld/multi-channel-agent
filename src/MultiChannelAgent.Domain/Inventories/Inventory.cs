namespace MultiChannelAgent.Domain.Inventories;

/// <summary>
/// A named collection of stock and locations with one Owner. Created explicitly by an active tenant
/// member, who atomically becomes its Owner. <see cref="ClientRequestId"/> is the stable idempotency
/// key supplied by the creating client: resubmitting the same (creator, ClientRequestId) pair must
/// return this same Inventory rather than creating another.
/// </summary>
public sealed record Inventory
{
    /// <summary>
    /// The authoritative maximum length for <see cref="Name"/>, matching the EF Core column's
    /// <c>HasMaxLength</c> configuration so an oversized name is rejected here - as a domain
    /// validation error - long before it could ever reach the database as an unhandled
    /// <see cref="System.Exception"/>.
    /// </summary>
    public const int MaxNameLength = 200;

    /// <summary>
    /// The authoritative maximum length for <see cref="ClientRequestId"/>, matching the EF Core
    /// column's <c>HasMaxLength</c> configuration for the same reason as <see cref="MaxNameLength"/>.
    /// </summary>
    public const int MaxClientRequestIdLength = 100;

    public required InventoryId Id { get; init; }

    public required string Name { get; init; }

    public required ParticipantId CreatedByParticipantId { get; init; }

    public required string ClientRequestId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public static Inventory Create(
        string name,
        ParticipantId createdBy,
        string clientRequestId,
        DateTimeOffset createdAt)
    {
        return new Inventory
        {
            Id = new InventoryId(Guid.NewGuid()),
            Name = RequireWithinBounds(RequireNonBlank(name, nameof(name)), MaxNameLength, nameof(name)),
            CreatedByParticipantId = createdBy,
            ClientRequestId = RequireWithinBounds(
                RequireNonBlank(clientRequestId, nameof(clientRequestId)), MaxClientRequestIdLength, nameof(clientRequestId)),
            CreatedAt = createdAt,
        };
    }

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
}
