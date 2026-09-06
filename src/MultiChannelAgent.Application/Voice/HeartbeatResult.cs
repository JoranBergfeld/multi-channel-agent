namespace MultiChannelAgent.Application.Voice;

/// <summary>
/// Outcome of a voice session heartbeat: whether the heartbeat was renewed, the authoritative
/// lifecycle state, remaining whole seconds until session expiry, and any forced-close reason.
/// </summary>
public sealed record HeartbeatResult(
    bool Renewed,
    HeartbeatLifecycleState LifecycleState,
    int? RemainingSeconds,
    string? ForcedCloseReason);
