namespace MultiChannelAgent.Application.Channels.Email;

/// <summary>Configuration for the internal email adapter's single inbound mailbox and webhook secret.</summary>
public sealed class EmailChannelOptions
{
    public required string MailboxAddress { get; init; }

    public required string ClientState { get; init; }
}
