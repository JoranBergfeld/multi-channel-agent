namespace MultiChannelAgent.Domain.Inventories;

/// <summary>
/// A named collection of stock and locations with one Owner. Created explicitly by an active tenant
/// member, who atomically becomes its Owner. <see cref="ClientRequestId"/> is the stable idempotency
/// key supplied by the creating client: resubmitting the same (creator, ClientRequestId) pair must
/// return this same Inventory rather than creating another.
/// </summary>
public sealed record Inventory
{
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
            Name = RequireNonBlank(name, nameof(name)),
            CreatedByParticipantId = createdBy,
            ClientRequestId = RequireNonBlank(clientRequestId, nameof(clientRequestId)),
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
}
