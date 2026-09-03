using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Domain.Tests;

public class InboundTurnTests
{
    private static readonly ParticipantId SomeParticipant = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));

    [Fact]
    public void Create_normalizes_whitespace_padded_fields()
    {
        var receivedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var turn = InboundTurn.Create(
            nativeMessageId: "  native-123  ",
            participantId: SomeParticipant,
            channelConversationId: "  conversation-abc  ",
            contentText: "  hello world  ",
            locale: "  en-US  ",
            receivedAt: receivedAt,
            traceId: "  trace-1  ");

        Assert.Equal("native-123", turn.NativeMessageId);
        Assert.Equal(SomeParticipant, turn.ParticipantId);
        Assert.Equal("conversation-abc", turn.ChannelConversationId.Value);
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
            participantId: SomeParticipant,
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
            participantId: SomeParticipant,
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
            participantId: SomeParticipant,
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
            participantId: SomeParticipant,
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
            participantId: SomeParticipant,
            channelConversationId: "conversation-abc",
            contentText: null!,
            locale: null,
            receivedAt: DateTimeOffset.UtcNow,
            traceId: null));
    }

    [Fact]
    public void Two_turns_created_for_the_same_native_message_get_distinct_turn_ids()
    {
        var first = InboundTurn.Create("native-123", SomeParticipant, "conversation-abc", "hello", null, DateTimeOffset.UtcNow, null);
        var second = InboundTurn.Create("native-123", SomeParticipant, "conversation-abc", "hello", null, DateTimeOffset.UtcNow, null);

        Assert.NotEqual(first.TurnId, second.TurnId);
    }

    // A native message identifier is only ever unique within the channel scope that issued it: two
    // different channel adapters (or two conversations within one adapter) can legitimately mint the
    // same opaque string. Deduplication therefore keys on the whole scope, never on the bare id.
    [Fact]
    public void The_native_message_key_carries_the_full_participant_and_conversation_scope()
    {
        var turn = InboundTurn.Create("native-123", SomeParticipant, "conversation-abc", "hello", null, DateTimeOffset.UtcNow, null);

        Assert.Equal(
            new NativeMessageKey(SomeParticipant, new ChannelConversationId("conversation-abc"), "native-123"),
            turn.NativeMessageKey);
    }

    [Fact]
    public void The_same_native_message_id_in_a_different_scope_is_a_different_key()
    {
        var otherParticipant = new ParticipantId(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        var receivedAt = DateTimeOffset.UtcNow;

        var mine = InboundTurn.Create("native-123", SomeParticipant, "conversation-abc", "hello", null, receivedAt, null);
        var otherConversation = InboundTurn.Create("native-123", SomeParticipant, "conversation-xyz", "hello", null, receivedAt, null);
        var otherParticipantTurn = InboundTurn.Create("native-123", otherParticipant, "conversation-abc", "hello", null, receivedAt, null);

        Assert.NotEqual(mine.NativeMessageKey, otherConversation.NativeMessageKey);
        Assert.NotEqual(mine.NativeMessageKey, otherParticipantTurn.NativeMessageKey);
    }
}
