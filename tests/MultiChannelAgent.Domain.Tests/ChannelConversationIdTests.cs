using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Domain.Tests;

public class ChannelConversationIdTests
{
    [Fact]
    public void ToString_returns_the_wrapped_value()
    {
        var id = new ChannelConversationId("conversation-abc");

        Assert.Equal("conversation-abc", id.ToString());
    }

    [Fact]
    public void Equal_values_are_equal_instances()
    {
        Assert.Equal(new ChannelConversationId("same"), new ChannelConversationId("same"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_throws_when_value_is_blank(string blank)
    {
        Assert.Throws<ArgumentException>(() => new ChannelConversationId(blank));
    }
}
