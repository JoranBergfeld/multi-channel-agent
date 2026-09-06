namespace MultiChannelAgent.Domain.Turns;

/// <summary>
/// How the Participant's input was captured. Set by the Host after validating trusted evidence
/// (e.g., an active voice session for <see cref="Voice"/>). Clients cannot attest modality directly;
/// it is always derived from server-side state the Host controls.
/// </summary>
public enum InputModality
{
    /// <summary>Typed text input — the default for all existing channels.</summary>
    Text = 0,

    /// <summary>Speech input via an active, server-validated voice session.</summary>
    Voice = 1,
}
