using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Domain.Tests;

public class InboundTurnTests
{
    [Fact]
    public void Create_normalizes_whitespace_padded_fields()
    {
        var receivedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var turn = InboundTurn.Create(
            nativeMessageId: "  native-123  ",
            channelConversationId: "  conversation-abc  ",
            contentText: "  hello world  ",
            locale: "  en-US  ",
            receivedAt: receivedAt,
            traceId: "  trace-1  ");

        Assert.Equal("native-123", turn.NativeMessageId);
        Assert.Equal("conversation-abc", turn.ChannelConversationId);
        Assert.Equal("hello world", turn.ContentText);
        Assert.Equal("en-US", turn.Locale);
        Assert.Equal("trace-1", turn.TraceId);
        Assert.Equal(receivedAt, turn.ReceivedAt);
        Assert.NotEqual(default, turn.TurnId.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_blank_native_message_id(string blank)
    {
        Assert.Throws<ArgumentException>(() => InboundTurn.Create(
            nativeMessageId: blank,
            channelConversationId: "conversation-abc",
            contentText: "hello",
            locale: null,
            receivedAt: DateTimeOffset.UtcNow,
            traceId: null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_blank_content_text(string blank)
    {
        Assert.Throws<ArgumentException>(() => InboundTurn.Create(
            nativeMessageId: "native-123",
            channelConversationId: "conversation-abc",
            contentText: blank,
            locale: null,
            receivedAt: DateTimeOffset.UtcNow,
            traceId: null));
    }

    // Non-HTTP callers (a null NativeMessageId/ChannelConversationId/ContentText can arrive from any
    // adapter, not just a JSON-bound HTTP request) must get the same clear ArgumentException a blank
    // string produces, never an NRE from Trim() on a null reference.
    [Fact]
    public void Create_rejects_null_native_message_id_without_throwing_a_null_reference_exception()
    {
        Assert.Throws<ArgumentException>(() => InboundTurn.Create(
            nativeMessageId: null!,
            channelConversationId: "conversation-abc",
            contentText: "hello",
            locale: null,
            receivedAt: DateTimeOffset.UtcNow,
            traceId: null));
    }

    [Fact]
    public void Create_rejects_null_channel_conversation_id_without_throwing_a_null_reference_exception()
    {
        Assert.Throws<ArgumentException>(() => InboundTurn.Create(
            nativeMessageId: "native-123",
            channelConversationId: null!,
            contentText: "hello",
            locale: null,
            receivedAt: DateTimeOffset.UtcNow,
            traceId: null));
    }

    [Fact]
    public void Create_rejects_null_content_text_without_throwing_a_null_reference_exception()
    {
        Assert.Throws<ArgumentException>(() => InboundTurn.Create(
            nativeMessageId: "native-123",
            channelConversationId: "conversation-abc",
            contentText: null!,
            locale: null,
            receivedAt: DateTimeOffset.UtcNow,
            traceId: null));
    }

    [Fact]
    public void Two_turns_created_for_the_same_native_message_get_distinct_turn_ids()
    {
        var first = InboundTurn.Create("native-123", "conversation-abc", "hello", null, DateTimeOffset.UtcNow, null);
        var second = InboundTurn.Create("native-123", "conversation-abc", "hello", null, DateTimeOffset.UtcNow, null);

        Assert.NotEqual(first.TurnId, second.TurnId);
    }
}
