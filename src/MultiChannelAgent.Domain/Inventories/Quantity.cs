namespace MultiChannelAgent.Domain.Inventories;

/// <summary>
/// A non-negative decimal amount paired with a Unit. The exact <see cref="Value"/> is preserved
/// (never rounded or converted) - only the Unit a Stock Entry references determines what the amount
/// means; different Units are never automatically converted between each other.
/// </summary>
public readonly record struct Quantity
{
    public decimal Value { get; }

    private Quantity(decimal value) => Value = value;

    /// <summary>On-hand Stock is exactly Stock Entries whose Quantity is greater than zero.</summary>
    public bool IsOnHand => Value > 0m;

    public static Quantity Create(decimal value)
    {
        if (value < 0m)
        {
            throw new ArgumentException("Quantity must not be negative.", nameof(value));
        }

        return new Quantity(value);
    }

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
