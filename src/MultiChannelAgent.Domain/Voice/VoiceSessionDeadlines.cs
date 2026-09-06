namespace MultiChannelAgent.Domain.Voice;

/// <summary>
/// Immutable deadline timestamps computed from the admission instant.
/// Config changes never retroactively alter admitted sessions.
/// </summary>
/// <param name="ExpiresAt">Absolute session expiry.</param>
/// <param name="WarningAt">Absolute warning instant.</param>
/// <param name="IdleExpiresAt">Absolute idle-close deadline.</param>
public sealed record VoiceSessionDeadlines(DateTimeOffset ExpiresAt, DateTimeOffset WarningAt, DateTimeOffset IdleExpiresAt);
