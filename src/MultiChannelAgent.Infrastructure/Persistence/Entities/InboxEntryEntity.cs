using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Infrastructure.Persistence.Entities;

/// <summary>
/// The durable inbox row for one accepted Turn. (<see cref="ParticipantId"/>,
/// <see cref="ChannelConversationId"/>, <see cref="NativeMessageId"/>) carries a unique constraint so
/// duplicate at-least-once delivery cannot create a second row (idempotency at the Turn boundary),
/// scoped the way a native message id is actually unique. <see cref="Status"/> tracks workflow
/// processing state.
/// </summary>
public sealed class InboxEntryEntity
{
    public Guid TurnId { get; set; }

    public required string NativeMessageId { get; set; }

    public Guid ParticipantId { get; set; }

    public required string ChannelConversationId { get; set; }

    /// <summary>
    /// The durable, strictly increasing acceptance order of this Turn within its
    /// <see cref="ChannelConversationId"/>, assigned once at acceptance and never reused. Wall-clock
    /// <see cref="ReceivedAt"/> is not an ordering key: two Turns can share an instant (or arrive out
    /// of clock order across replicas), so this is what deterministically orders a conversation.
    /// </summary>
    public long ConversationSequence { get; set; }

    /// <summary>Which channel this Turn arrived on, for example <c>web</c>.</summary>
    public required string Channel { get; set; }

    /// <summary>How the channel authenticated the Participant behind this Turn (evidence, never authorization).</summary>
    public ChannelPrincipalKind PrincipalKind { get; set; }

    /// <summary>The channel's own authenticated subject - an Entra object id, a verified mailbox address.</summary>
    public required string PrincipalSubject { get; set; }

    public string? PrincipalTenantId { get; set; }

    /// <summary>What the channel can render and carry, as declared by its adapter with the Turn.</summary>
    public ChannelCapabilities Capabilities { get; set; }

    public string? Locale { get; set; }

    public string? TraceId { get; set; }

    /// <summary>
    /// Whether the channel reported this utterance as interrupted. Durable because it is part of what
    /// the Turn <em>is</em>: a Turn re-driven after a restart must still refuse to confirm anything.
    /// </summary>
    public bool WasInterrupted { get; set; }

    /// <summary>
    /// The Foundry conversation this Turn was accepted into, captured at acceptance. Nullable only
    /// for Turns accepted before this was recorded; every acceptance since writes it, which is what
    /// stops a conversation reset from moving already-accepted work into the new history.
    /// </summary>
    public Guid? FoundryConversationId { get; set; }

    /// <summary>The generation of <see cref="FoundryConversationId"/> at the moment this Turn was accepted.</summary>
    public int? FoundryConversationGeneration { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>
    /// <see cref="ReceivedAt"/> as UTC ticks. Claiming orders candidate heads by how long each has
    /// been waiting, and a DateTimeOffset is not orderable on every relational provider this model
    /// runs on, so the same instant is also kept in a form every provider can sort. It is written
    /// once, at acceptance, from that very value.
    /// </summary>
    public long ReceivedAtTicks { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public InboxEntryStatus Status { get; set; }
}

public enum InboxEntryStatus
{
    Pending = 0,
    Completed = 1,
}
