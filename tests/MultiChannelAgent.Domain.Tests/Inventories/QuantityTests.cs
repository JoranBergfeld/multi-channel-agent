using System.Globalization;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Domain.Tests.Inventories;

public class QuantityTests
{
    [Fact]
    public void Create_accepts_zero()
    {
        var quantity = Quantity.Create(0m);

        Assert.Equal(0m, quantity.Value);
        Assert.False(quantity.IsOnHand);
    }

    [Fact]
    public void Create_accepts_an_exact_positive_decimal_without_precision_loss()
    {
        var quantity = Quantity.Create(12.375m);

        Assert.Equal(12.375m, quantity.Value);
        Assert.True(quantity.IsOnHand);
    }

    [Fact]
    public void Create_rejects_a_negative_value()
    {
        Assert.Throws<ArgumentException>(() => Quantity.Create(-0.01m));
    }
    // The text a Participant and every channel sees must depend only on the amount, never on how a
    // particular database happened to materialize it: SQL Server hands back a decimal(28,10) with its
    // scale intact (12.0000000000) where SQLite hands back 12, and a caller comparing "12" would
    // silently disagree with itself across providers.
    [Theory]
    [InlineData("12.0000000000", "12")]
    [InlineData("12", "12")]
    [InlineData("0.0000000000", "0")]
    [InlineData("0", "0")]
    [InlineData("12.3750000000", "12.375")]
    [InlineData("12.375", "12.375")]
    [InlineData("0.5000000000", "0.5")]
    public void Invariant_text_depends_only_on_the_amount_not_on_its_recorded_scale(string recorded, string expected)
    {
        var quantity = Quantity.Create(decimal.Parse(recorded, CultureInfo.InvariantCulture));

        Assert.Equal(expected, quantity.ToInvariantText());
    }

    // Precision that carries meaning is never dropped, and it is never rendered in scientific
    // notation - "1E-10" is not a quantity anyone can read, type back, or parse as decimal text.
    [Fact]
    public void A_small_fractional_amount_keeps_every_significant_digit_as_plain_decimal_text()
    {
        var quantity = Quantity.Create(decimal.Parse("0.0000000001", CultureInfo.InvariantCulture));

        Assert.Equal("0.0000000001", quantity.ToInvariantText());
    }

    [Fact]
    public void The_same_amount_recorded_at_different_scales_reads_identically()
    {
        var scaled = Quantity.Create(decimal.Parse("7.5000000000", CultureInfo.InvariantCulture));
        var plain = Quantity.Create(7.5m);

        Assert.Equal(plain.ToInvariantText(), scaled.ToInvariantText());
    }

    [Fact]
    public void The_string_representation_is_that_same_invariant_text()
    {
        var quantity = Quantity.Create(decimal.Parse("12.0000000000", CultureInfo.InvariantCulture));

        Assert.Equal(quantity.ToInvariantText(), quantity.ToString());
    }

    [Theory]
    [InlineData("0", "0")]
    [InlineData("12", "12")]
    [InlineData("12.5", "12.5")]
    [InlineData("  12.50  ", "12.5")]
    [InlineData("0.0000000001", "0.0000000001")]
    public void Invariant_decimal_text_parses_to_that_exact_amount(string text, string expected)
    {
        Assert.True(Quantity.TryParseInvariant(text, out var quantity));
        Assert.Equal(expected, quantity.ToInvariantText());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("1,5")]
    [InlineData("1 000")]
    [InlineData("1e3")]
    [InlineData("-1")]
    [InlineData("0.00000000001")]
    [InlineData("1000000000000000000")]
    public void Text_that_is_not_a_storable_non_negative_decimal_does_not_parse(string? text)
    {
        Assert.False(Quantity.TryParseInvariant(text, out var quantity));
        Assert.Equal("0", quantity.ToInvariantText());
    }

    [Fact]
    public void Adding_two_amounts_keeps_every_decimal_digit()
    {
        Assert.True(Quantity.Create(12.5m).TryAdd(Quantity.Create(2.25m), out var sum));

        Assert.Equal("14.75", sum.ToInvariantText());
    }

    [Fact]
    public void Adding_beyond_the_storable_range_is_refused_rather_than_silently_wrapped()
    {
        var nearLimit = Quantity.Create(999_999_999_999_999_999m);

        Assert.False(nearLimit.TryAdd(Quantity.Create(1m), out _));
    }

    [Fact]
    public void Subtracting_within_the_amount_on_hand_keeps_every_decimal_digit()
    {
        Assert.True(Quantity.Create(14.75m).TrySubtract(Quantity.Create(4.75m), out var result));

        Assert.Equal("10", result.ToInvariantText());
    }

    [Fact]
    public void Subtracting_more_than_the_amount_on_hand_is_refused_and_never_goes_negative()
    {
        Assert.False(Quantity.Create(3m).TrySubtract(Quantity.Create(3.0000000001m), out var result));

        Assert.Equal("0", result.ToInvariantText());
    }

    [Fact]
    public void Zero_is_an_amount_that_is_not_on_hand()
    {
        Assert.Equal("0", Quantity.Zero.ToInvariantText());
        Assert.False(Quantity.Zero.IsOnHand);
    }
}
