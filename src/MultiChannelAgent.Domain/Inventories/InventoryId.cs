namespace MultiChannelAgent.Domain.Inventories;

/// <summary>
/// Strongly typed Inventory identity. <see cref="ShortId"/> is a deterministic, stable short form
/// (the first 8 hex characters of the identity) used together with the Owner's display name to
/// disambiguate Inventories that share a display name without exposing the full internal GUID.
/// </summary>
public readonly record struct InventoryId(Guid Value)
{
    public string ShortId => Value.ToString("N")[..8];

    public override string ToString() => Value.ToString();
}
