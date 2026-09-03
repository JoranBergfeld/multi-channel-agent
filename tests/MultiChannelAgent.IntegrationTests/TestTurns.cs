using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// Builds the ordinary text Turn a signed-in web Participant produces, so tests that are about
/// something else (ordering, idempotency, processing) do not have to restate the whole inbound
/// contract each time. Anything a test genuinely cares about - other provenance, other capabilities -
/// it still states explicitly through <see cref="InboundTurnDraft"/>.
/// </summary>
internal static class TestTurns
{
    public static InboundTurn Text(
        string? nativeMessageId,
        ParticipantId participantId,
        string? channelConversationId,
        string? contentText,
        string? locale,
        DateTimeOffset receivedAt,
        string? traceId) =>
        InboundTurn.Create(InboundTurnDraft.DirectText(
            nativeMessageId,
            participantId,
            channelConversationId,
            "web",
            ChannelPrincipal.EntraUser(participantId.Value.ToString(), "22222222-2222-2222-2222-222222222222"),
            ChannelCapabilities.Text | ChannelCapabilities.RichText | ChannelCapabilities.ProgressEvents,
            contentText,
            locale,
            receivedAt,
            traceId));
}
