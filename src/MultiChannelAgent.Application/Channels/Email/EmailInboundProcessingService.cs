using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Channels.Email;

/// <summary>
/// Turns one Graph email notification into a durably accepted channel-neutral Turn when the message
/// exists and its sender resolves to an active tenant member.
/// </summary>
public sealed class EmailInboundProcessingService(
    IGraphMailboxClient graphMailboxClient,
    EmailSenderResolutionService senderResolutionService,
    IParticipantStore participantStore,
    IInboxStore inboxStore,
    IFoundryConversationBindingStore bindingStore)
{
    public async Task<EmailProcessingOutcome> ProcessAsync(string messageId, CancellationToken cancellationToken)
    {
        var message = await graphMailboxClient.GetMessageAsync(messageId, cancellationToken);
        if (message is null)
        {
            return new EmailProcessingOutcome.MessageNotFound(messageId);
        }

        var resolvedSender = await senderResolutionService.ResolveMemberAsync(message.SenderAddress, cancellationToken);
        if (resolvedSender is null)
        {
            return new EmailProcessingOutcome.SenderUnresolved(message.SenderAddress);
        }

        var participantId = resolvedSender.ParticipantId;
        var conversationId = new ChannelConversationId(message.ConversationId);
        var nativeMessageKey = new NativeMessageKey(participantId, conversationId, message.ImmutableId);
        var existing = await inboxStore.FindByNativeMessageIdAsync(nativeMessageKey, cancellationToken);
        if (existing is not null)
        {
            return new EmailProcessingOutcome.Accepted(new InboxAcceptResult(existing, WasAlreadyAccepted: true));
        }

        await participantStore.UpsertAsync(
            Participant.Create(participantId, resolvedSender.DisplayName),
            cancellationToken);

        var draft = new InboundTurnDraft
        {
            NativeMessageId = message.ImmutableId,
            ParticipantId = participantId,
            ChannelConversationId = message.ConversationId,
            Channel = EmailChannel.Name,
            Principal = ChannelPrincipal.ExchangeMailbox(message.SenderAddress, tenantId: null),
            Capabilities = ChannelCapabilities.Text,
            ContentParts = BuildContentParts(message),
            ReceivedAt = message.ReceivedAt,
        };

        var turn = InboundTurn.Create(draft);
        var binding = await bindingStore.GetOrCreateAsync(
            participantId,
            conversationId,
            message.ReceivedAt,
            cancellationToken);

        var accepted = await inboxStore.AcceptAsync(turn, binding, cancellationToken);
        return new EmailProcessingOutcome.Accepted(accepted);
    }

    private static IReadOnlyList<TurnContentPart> BuildContentParts(GraphInboundMessage message)
    {
        var parts = new List<TurnContentPart>
        {
            TurnContentPart.Create(1, ContentProvenance.Direct, message.DirectText),
        };

        if (message.QuotedText is { } quotedText)
        {
            parts.Add(TurnContentPart.Create(2, ContentProvenance.Quoted, quotedText));
        }

        return parts;
    }
}

/// <summary>The durable outcome of processing one Graph mailbox notification entry.</summary>
public abstract record EmailProcessingOutcome
{
    private EmailProcessingOutcome()
    {
    }

    public sealed record Accepted(InboxAcceptResult Result) : EmailProcessingOutcome;

    public sealed record SenderUnresolved(string SenderAddress) : EmailProcessingOutcome;

    public sealed record MessageNotFound(string MessageId) : EmailProcessingOutcome;
}
