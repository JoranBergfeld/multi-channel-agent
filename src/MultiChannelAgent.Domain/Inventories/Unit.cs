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
/// An Inventory-owned controlled measure. Every Inventory starts with exactly one reserved Unit:
/// the canonical `each`, with the fixed aliases `piece`, `pieces`, `pc`, and `pcs`. Unit names and
/// aliases share one collision-free namespace within an Inventory.
/// </summary>
public sealed record Unit
{
    public static readonly IReadOnlyList<string> ReservedEachAliases = ["piece", "pieces", "pc", "pcs"];

    public required UnitId Id { get; init; }

    public required InventoryId InventoryId { get; init; }

    public required string CanonicalName { get; init; }

    public required bool IsReserved { get; init; }

    public required IReadOnlyList<string> Aliases { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public static Unit CreateReservedEach(InventoryId inventoryId, DateTimeOffset createdAt) => new()
    {
        Id = new UnitId(Guid.NewGuid()),
        InventoryId = inventoryId,
        CanonicalName = "each",
        IsReserved = true,
        Aliases = ReservedEachAliases,
        CreatedAt = createdAt,
    };
}
