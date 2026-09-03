namespace MultiChannelAgent.Domain.Turns;

/// <summary>
/// Where one piece of a Turn's content came from. Only <see cref="Direct"/> content - authored by the
/// authenticated Participant in this very Turn - may ever provide operational intent; everything else
/// is data the agent may read but can never be authorized by. Preserving that distinction on the
/// content itself is what lets deterministic code enforce it, instead of hoping a model does.
/// </summary>
public enum ContentProvenance
{
    /// <summary>Authored by the authenticated Participant in this Turn.</summary>
    Direct,

    /// <summary>Quoted from earlier conversation content.</summary>
    Quoted,

    /// <summary>Forwarded from another message or thread.</summary>
    Forwarded,

    /// <summary>Carried by an attachment.</summary>
    Attached,

    /// <summary>Reached through a link the content carried.</summary>
    Linked,

    /// <summary>Retrieved from a store or index rather than authored here.</summary>
    Retrieved,

    /// <summary>Produced by a tool.</summary>
    ToolProduced,

    /// <summary>Produced by the model.</summary>
    ModelDerived,
}

/// <summary>
/// One ordered piece of a Turn's content, with the provenance that decides what it may be used for.
/// <see cref="Order"/> is the position within the Turn (1-based), preserved durably so a channel's
/// content is never reassembled in a different order than the Participant sent it.
/// </summary>
public sealed record TurnContentPart
{
    /// <summary>The authoritative maximum length of one part, matching the persisted column.</summary>
    public const int MaxTextLength = 32 * 1024;

    public required int Order { get; init; }

    public required ContentProvenance Provenance { get; init; }

    public required string Text { get; init; }

    public static TurnContentPart Create(int order, ContentProvenance provenance, string? text)
    {
        if (order < 1)
        {
            throw new ArgumentException("Order must be 1 or greater.", nameof(order));
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Value must not be blank.", nameof(text));
        }

        var trimmed = text.Trim();
        if (trimmed.Length > MaxTextLength)
        {
            throw new ArgumentException($"Value must not exceed {MaxTextLength} characters.", nameof(text));
        }

        return new TurnContentPart { Order = order, Provenance = provenance, Text = trimmed };
    }
}
