namespace MultiChannelAgent.Application.Channels.Email;

/// <summary>Graph access for the single dedicated inbound mailbox, isolated so tests never call Graph.</summary>
public interface IGraphMailboxClient
{
    Task<GraphInboundMessage?> GetMessageAsync(string messageId, CancellationToken cancellationToken);

    Task SendReplyAsync(string messageId, string replyText, CancellationToken cancellationToken);
}
