using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Domain.Tests;

public sealed class ChannelPrincipalTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ExchangeMailbox_rejects_a_blank_subject(string blank) =>
        Assert.Throws<ArgumentException>(() => ChannelPrincipal.ExchangeMailbox(blank, "tenant-123"));

    [Fact]
    public void ExchangeMailbox_round_trips_a_valid_subject_and_normalized_tenant()
    {
        var principal = ChannelPrincipal.ExchangeMailbox(" user@example.com ", " tenant-123 ");

        Assert.Equal(ChannelPrincipalKind.ExchangeMailbox, principal.Kind);
        Assert.Equal("user@example.com", principal.Subject);
        Assert.Equal("tenant-123", principal.TenantId);
    }
}
