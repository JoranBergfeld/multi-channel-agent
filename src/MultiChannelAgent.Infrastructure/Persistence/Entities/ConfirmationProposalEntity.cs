namespace MultiChannelAgent.Infrastructure.Persistence.Entities;

/// <summary>
/// The durable row for one confirmation proposal. It carries the hash of its single-use token - never
/// the token - its binding, its exact serialized contents, and its lifetime. The plaintext lives only
/// in the answer that asks for confirmation; see <c>ConfirmationToken</c>.
///
/// Only <see cref="Status"/> ever changes after insert, and only ever from <c>Pending</c> to a
/// terminal value, which is what makes single use enforceable by a guarded update rather than by
/// hoping two callers do not race.
/// </summary>
public sealed class ConfirmationProposalEntity
{
    public Guid ProposalId { get; set; }

    /// <summary>SHA-256 of the token, as 64 lowercase hexadecimal characters. Unique, so a token can never back two proposals.</summary>
    public required string TokenHash { get; set; }

    public Guid ParticipantId { get; set; }

    public required string ChannelConversationId { get; set; }

    public Guid InventoryId { get; set; }

    public Guid ProposedInTurnId { get; set; }

    /// <summary>The <c>ProposalStatus</c> as text, so the filtered unique index can be written in provider-neutral SQL.</summary>
    public required string Status { get; set; }

    /// <summary>The <c>ProposalKind</c> as text: which of the two disjoint payloads this row carries.</summary>
    public required string Kind { get; set; }

    /// <summary>The exact proposed administration changes, serialized; null for a stock proposal.</summary>
    public string? ReferenceChangesJson { get; set; }

    /// <summary>The expected Unit and Location versions, serialized; null for a stock proposal.</summary>
    public string? ExpectedReferenceVersionsJson { get; set; }

    /// <summary>The normalized terms this proposal expects to still be free, serialized; null for a stock proposal.</summary>
    public string? ExpectedTermAbsencesJson { get; set; }

    /// <summary>The exact proposed changes, serialized (see <c>ConfirmationProposalMapper</c>). What the Participant reviewed is what commits.</summary>
    public required string ChangesJson { get; set; }

    public required string ExpectedVersionsJson { get; set; }

    public required string ExpectedAbsencesJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// <see cref="ExpiresAt"/> as UTC ticks. The expiry sweep compares and orders by it, and a
    /// DateTimeOffset is not comparable on every relational provider this model runs on, so the same
    /// instant is also kept in a form every provider can compare. It is written once, at insert, from
    /// that very value.
    /// </summary>
    public long ExpiresAtTicks { get; set; }

    /// <summary>When it left <c>Pending</c>; null while it is still pending. Retention is measured from here.</summary>
    public DateTimeOffset? SettledAt { get; set; }

    /// <summary><see cref="SettledAt"/> as UTC ticks, for the same reason as <see cref="ExpiresAtTicks"/>.</summary>
    public long? SettledAtTicks { get; set; }
}
