using System.Globalization;

namespace MultiChannelAgent.Domain.Inventories;

/// <summary>
/// A non-negative decimal amount paired with a Unit. The exact <see cref="Value"/> is preserved
/// (never rounded or converted) - only the Unit a Stock Entry references determines what the amount
/// means; different Units are never automatically converted between each other.
///
/// <see cref="ToInvariantText"/> is the one way an amount is ever rendered outside this domain, and
/// <see cref="TryParseInvariant"/> is the one way untrusted text ever becomes one, so what a
/// Participant, a channel, or a tool argument sees depends only on the amount itself.
/// </summary>
public readonly record struct Quantity
{
    /// <summary>
    /// The most digits an amount may carry before the decimal point, and the most after it. These are
    /// the domain's own limits, chosen to match what the authoritative column
    /// (<c>decimal(28,10)</c>) can hold exactly, so an amount that could not be stored is refused as a
    /// domain rule rather than discovered as a truncation or overflow at the database.
    /// </summary>
    public const int MaxIntegerDigits = 18;

    /// <summary>The most digits an amount may carry after the decimal point. See <see cref="MaxIntegerDigits"/>.</summary>
    public const int MaxScale = 10;

    private const decimal IntegerDigitLimit = 1_000_000_000_000_000_000m;

    public decimal Value { get; }

    private Quantity(decimal value) => Value = value;

    /// <summary>The amount that is not on hand at all. Every Set to this amount is a deliberate, confirmed act.</summary>
    public static Quantity Zero { get; } = new(0m);

    /// <summary>On-hand Stock is exactly Stock Entries whose Quantity is greater than zero.</summary>
    public bool IsOnHand => Value > 0m;

    public static Quantity Create(decimal value)
    {
        if (value < 0m)
        {
            throw new ArgumentException("Quantity must not be negative.", nameof(value));
        }

        if (!IsStorable(value))
        {
            throw new ArgumentException(
                $"Quantity must have at most {MaxIntegerDigits} digits before the decimal point and {MaxScale} after it.",
                nameof(value));
        }

        return new Quantity(value);
    }

    /// <summary>
    /// Reads the one text form this domain ever exchanges an amount in: plain, culture-invariant
    /// decimal notation. Grouping separators, locale decimal commas, and scientific notation are all
    /// refused rather than guessed at, because each of them means different amounts to different
    /// readers - and so is anything negative or larger than the amount can be stored exactly.
    /// </summary>
    public static bool TryParseInvariant(string? text, out Quantity quantity)
    {
        quantity = Zero;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        const NumberStyles PlainDecimal = NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign;
        if (!decimal.TryParse(text.Trim(), PlainDecimal, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        if (value < 0m || !IsStorable(value))
        {
            return false;
        }

        quantity = new Quantity(value);
        return true;
    }

    /// <summary>
    /// Increases this amount, refusing rather than wrapping or rounding when the sum could no longer
    /// be stored exactly. <paramref name="result"/> is <see cref="Zero"/> when it refuses, so a caller
    /// that ignores the return value cannot silently write a wrong amount.
    /// </summary>
    public bool TryAdd(Quantity addend, out Quantity result)
    {
        result = Zero;

        var sum = Value + addend.Value;
        if (!IsStorable(sum))
        {
            return false;
        }

        result = new Quantity(sum);
        return true;
    }

    /// <summary>
    /// Decreases this amount, refusing when the subtrahend exceeds it - Quantity is never negative, so
    /// an over-large Remove is a refusal rather than a negative amount.
    /// </summary>
    public bool TrySubtract(Quantity subtrahend, out Quantity result)
    {
        result = Zero;

        if (subtrahend.Value > Value)
        {
            return false;
        }

        var difference = Value - subtrahend.Value;
        if (!IsStorable(difference))
        {
            return false;
        }

        result = new Quantity(difference);
        return true;
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

    /// <summary>
    /// True when the amount fits the exact decimal shape this domain guarantees. Trailing zeros are
    /// dropped first, so an amount that is only incidentally carried at a wide scale (as a database
    /// hands one back) is judged by the digits it actually has.
    /// </summary>
    private static bool IsStorable(decimal value)
    {
        var normalized = Normalized(value);
        return ScaleOf(normalized) <= MaxScale && Math.Abs(decimal.Truncate(normalized)) < IntegerDigitLimit;
    }

    private static int ScaleOf(decimal value) => (decimal.GetBits(value)[3] >> 16) & 0xFF;

    private static decimal Normalized(decimal value) => value / 1.000000000000000000000000000000000m;
}
