using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Infrastructure.Persistence.Entities;

/// <summary>
/// One durable, ordered piece of an accepted Turn's content, with the provenance that decides what it
/// may be used for. Stored as its own rows rather than an opaque blob so ordering and provenance stay
/// first-class - the trust boundary depends on both, and a blob would make them a parsing detail.
/// </summary>
public sealed class InboxContentPartEntity
{
    public Guid TurnId { get; set; }

    public int Order { get; set; }

    public ContentProvenance Provenance { get; set; }

    public required string Text { get; set; }
}
