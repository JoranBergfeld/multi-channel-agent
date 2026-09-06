namespace MultiChannelAgent.Domain.Voice;

/// <summary>
/// Strongly typed Voice Session identity. Each session gets a newly generated, globally unique ID on admission.
/// </summary>
public readonly record struct VoiceSessionId(Guid Value)
{
    public override string ToString() => Value.ToString();
}
