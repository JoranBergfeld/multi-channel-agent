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
}
