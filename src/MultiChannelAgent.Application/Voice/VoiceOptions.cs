using MultiChannelAgent.Domain.Voice;

namespace MultiChannelAgent.Application.Voice;

/// <summary>
/// Voice Live connection and session-lifetime settings.
///
/// These are capacity and session limits — they govern how many concurrent sessions are allowed and
/// how long each session may run. They are NOT monetary budget, spend, quota, or cost controls;
/// billing enforcement is outside the scope of this initial implementation.
///
/// Authentication is Entra <c>TokenCredential</c> only. API-key mode is excluded from initial scope.
/// </summary>
public sealed class VoiceOptions
{
    /// <summary>Whether the Voice Live feature is active. All other settings are ignored when <see langword="false"/>.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Absolute WSS URI of the Voice Live WebRTC endpoint.
    /// Must be under <c>.services.ai.azure.com</c> (primary) or <c>.cognitiveservices.azure.com</c> (legacy).
    /// Required when <see cref="Enabled"/> is <see langword="true"/>.
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>Azure AI model deployment name. Required when <see cref="Enabled"/> is <see langword="true"/>.</summary>
    public string? Model { get; set; }

    /// <summary>Azure AI voice synthesis voice name.</summary>
    public string VoiceName { get; set; } = "en-US-Ava:DragonHDLatestNeural";

    /// <summary>
    /// Maximum number of concurrently active Voice Live sessions across all Participants.
    /// This is a capacity limit, not a monetary quota. Must be at least 1.
    /// </summary>
    public int GlobalActiveCap { get; set; } = 5;

    /// <summary>Maximum duration a single session may remain active after admission. Must be positive.</summary>
    public TimeSpan MaxSessionDuration { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Duration from admission at which a session-expiry warning is issued.
    /// This is the warning instant (admission + threshold), not a "remaining time" duration.
    /// Must be greater than zero and strictly less than <see cref="MaxSessionDuration"/>.
    /// </summary>
    public TimeSpan SessionWarningThreshold { get; set; } = TimeSpan.FromMinutes(25);

    /// <summary>Duration of inactivity after which a session is automatically closed. Must be positive.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Interval between keep-alive heartbeats. Must be positive and strictly less than
    /// <see cref="IdleTimeout"/> so a heartbeat always arrives before idle expiry.
    /// </summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Validates this configuration and returns all detected problems.
    /// Returns an empty collection when <see cref="Enabled"/> is <see langword="false"/> — disabled
    /// voice imposes no constraints on unset fields.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        if (!Enabled)
        {
            return [];
        }

        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Endpoint))
        {
            errors.Add("Voice:Endpoint is required when voice is enabled.");
        }
        else
        {
            ValidateEndpointUri(Endpoint, errors);
        }

        if (string.IsNullOrWhiteSpace(Model))
        {
            errors.Add("Voice:Model is required when voice is enabled.");
        }

        if (GlobalActiveCap < 1)
        {
            errors.Add("Voice:GlobalActiveCap must be at least 1.");
        }

        if (MaxSessionDuration <= TimeSpan.Zero)
        {
            errors.Add("Voice:MaxSessionDuration must be positive.");
        }

        if (SessionWarningThreshold <= TimeSpan.Zero || SessionWarningThreshold >= MaxSessionDuration)
        {
            errors.Add("Voice:SessionWarningThreshold must be greater than zero and strictly less than MaxSessionDuration.");
        }

        if (IdleTimeout <= TimeSpan.Zero)
        {
            errors.Add("Voice:IdleTimeout must be positive.");
        }

        if (HeartbeatInterval <= TimeSpan.Zero || HeartbeatInterval >= IdleTimeout)
        {
            errors.Add("Voice:HeartbeatInterval must be positive and strictly less than IdleTimeout.");
        }

        return errors;
    }

    /// <summary>
    /// Computes immutable deadline timestamps for a session admitted at <paramref name="admittedAt"/>.
    /// Snapshots the current option values so later config changes do not affect already-admitted sessions.
    /// </summary>
    public VoiceSessionDeadlines ComputeDeadlines(DateTimeOffset admittedAt) => new(
        ExpiresAt: admittedAt + MaxSessionDuration,
        WarningAt: admittedAt + SessionWarningThreshold,
        IdleExpiresAt: admittedAt + IdleTimeout);

    private static void ValidateEndpointUri(string endpoint, List<string> errors)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Voice:Endpoint must be an absolute WSS URI (wss://).");
            return;
        }

        var host = uri.Host;
        if (!host.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase)
            && !host.EndsWith(".cognitiveservices.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(
                "Voice:Endpoint host must be under .services.ai.azure.com (primary) or .cognitiveservices.azure.com (legacy).");
        }
    }
}
