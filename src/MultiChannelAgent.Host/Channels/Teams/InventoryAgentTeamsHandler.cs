using System.Text.Json;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.Compat;
using Microsoft.Agents.Core.Models;
using MultiChannelAgent.Application.Channels.Teams;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Host.Channels.Teams;

/// <summary>Translates validated Teams personal-chat message activities into channel-neutral Turns.</summary>
public sealed class InventoryAgentTeamsHandler(
    ITeamsConversationReferenceStore conversationReferenceStore,
    IParticipantStore participantStore,
    IFoundryConversationBindingStore bindingStore,
    IInboxStore inboxStore,
    TimeProvider timeProvider) : ActivityHandler
{
    protected override async Task OnMessageActivityAsync(
        ITurnContext<IMessageActivity> turnContext,
        CancellationToken cancellationToken)
    {
        var activity = turnContext.Activity;
        if (activity.Conversation is not { Id: { Length: > 0 } conversationId })
        {
            return;
        }

        if (activity.Conversation.ConversationType is { Length: > 0 } conversationType
            && !string.Equals(conversationType, ConversationTypes.Personal, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var aadObjectId = activity.From?.AadObjectId;
        if (string.IsNullOrWhiteSpace(aadObjectId) || !Guid.TryParse(aadObjectId, out var participantGuid))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(activity.Text) || string.IsNullOrWhiteSpace(activity.Id))
        {
            return;
        }

        await conversationReferenceStore.SaveAsync(
            conversationId,
            JsonSerializer.Serialize(activity.GetConversationReference()),
            cancellationToken);

        var participantId = new ParticipantId(participantGuid);
        await participantStore.UpsertAsync(
            Participant.Create(participantId, activity.From?.Name ?? aadObjectId),
            cancellationToken);

        var receivedAt = timeProvider.GetUtcNow();
        var channelConversationId = new ChannelConversationId(conversationId);
        var turn = InboundTurn.Create(new InboundTurnDraft
        {
            NativeMessageId = activity.Id,
            ParticipantId = participantId,
            ChannelConversationId = conversationId,
            Channel = TeamsChannel.Name,
            Principal = ChannelPrincipal.EntraUser(aadObjectId, activity.Conversation.TenantId),
            Capabilities = ChannelCapabilities.Text,
            ContentParts = [TurnContentPart.Create(1, ContentProvenance.Direct, activity.Text)],
            ReceivedAt = receivedAt,
            InputModality = InputModality.Text,
        });

        var binding = await bindingStore.GetOrCreateAsync(
            participantId,
            channelConversationId,
            receivedAt,
            cancellationToken);

        await inboxStore.AcceptAsync(turn, binding, cancellationToken);
    }
}
