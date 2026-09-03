using System.Globalization;

namespace MultiChannelAgent.Domain.Inventories;

/// <summary>
/// A non-negative decimal amount paired with a Unit. The exact <see cref="Value"/> is preserved
/// (never rounded or converted) - only the Unit a Stock Entry references determines what the amount
/// means; different Units are never automatically converted between each other.
///
/// <see cref="ToInvariantText"/> is the one way an amount is ever rendered outside this domain, so
/// what a Participant, a channel, or a tool result shows depends only on the amount itself.
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

    /// <summary>
    /// The canonical, culture-invariant decimal text for this amount: exact, in plain decimal
    /// notation, and independent of the scale it happens to be carried at.
    ///
    /// A .NET decimal remembers its scale, and a database hands one back at its column's scale - SQL
    /// Server returns a decimal(28,10) as 12.0000000000 where SQLite returns 12 - so rendering the
    /// raw value would make the same amount read differently depending on where it was stored, and
    /// any caller comparing that text would disagree with itself across providers. Dividing by one at
    /// full precision drops only the trailing zeros, never a significant digit and never the value,
    /// and (unlike a general "G" format) never switches to scientific notation for small amounts,
    /// which is not decimal text anyone can read back or parse.
    /// </summary>
    public string ToInvariantText() => Normalized(Value).ToString(CultureInfo.InvariantCulture);

    public override string ToString() => ToInvariantText();

    private static decimal Normalized(decimal value) => value / 1.000000000000000000000000000000000m;
}
