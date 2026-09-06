namespace MultiChannelAgent.Application.Voice;

/// <summary>
/// Authoritative lifecycle state returned by a voice session heartbeat.
/// Maps to wire values active, warning_due, expired, idle, and not_found at the HTTP boundary.
/// </summary>
public enum HeartbeatLifecycleState
{
    Active,
    WarningDue,
    Expired,
    Idle,
    NotFound,
}
