namespace MultiChannelAgent.Domain.Voice;

/// <summary>
/// Lifecycle phases of a Voice session.
/// </summary>
public enum VoiceSessionStatus
{
    /// <summary>
    /// Session reserved; waiting for the control channel to be established.
    /// The slot is occupied and the participant cannot start a second session.
    /// </summary>
    Negotiating,

    /// <summary>
    /// Control channel established; session is live and heartbeats are expected.
    /// </summary>
    Active,

    /// <summary>
    /// Session has ended — either completed, abandoned during negotiation, or force-closed.
    /// The slot is released.
    /// </summary>
    Ended,
}
