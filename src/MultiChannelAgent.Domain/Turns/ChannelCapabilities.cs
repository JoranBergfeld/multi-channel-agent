namespace MultiChannelAgent.Domain.Turns;

/// <summary>
/// What the channel a Turn arrived on can actually do. Declared by its adapter with the Turn, so the
/// core can decide what to offer (progressive disclosure, attachments, voice) without ever knowing
/// which channel it is talking to. Absent capabilities are simply not offered - never assumed.
/// </summary>
[Flags]
public enum ChannelCapabilities
{
    None = 0,

    /// <summary>The channel can render plain text responses. Every channel has at least this.</summary>
    Text = 1,

    /// <summary>The channel can render structured/rich response parts, not only a flat string.</summary>
    RichText = 1 << 1,

    /// <summary>The channel can show progress before the terminal Outcome arrives.</summary>
    ProgressEvents = 1 << 2,

    /// <summary>The channel can carry attachments inbound.</summary>
    Attachments = 1 << 3,

    /// <summary>The channel supports interactive speech.</summary>
    Voice = 1 << 4,
}
