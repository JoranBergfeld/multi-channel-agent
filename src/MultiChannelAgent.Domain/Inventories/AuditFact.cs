namespace MultiChannelAgent.Domain.Inventories;

/// <summary>
/// The semantic audit event types this ticket produces. Each is a minimal fact about a governance
/// state change or a denied access attempt - never stock details, prompts/content, secrets, SQL
/// diagnostics, or raw payloads.
/// </summary>
public enum AuditEventType
{
    MembershipGranted,
    RoleChanged,
    MembershipRemoved,
    OwnershipTransferred,
    OrphanOwnershipRecovered,
    AccessDenied,
}

/// <summary>
/// Who performed the audited action. A Recovery Administrator is deliberately distinct from
/// <see cref="Participant"/> - they never hold Membership, so their identity is recorded as an actor
/// string (their trusted claim value), not a <see cref="ParticipantId"/>.
/// </summary>
public enum AuditActorKind
{
    Participant,
    RecoveryAdministrator,
}

/// <summary>
/// One immutable, minimal semantic audit fact: who did what to whom, on which Inventory, with what
/// outcome, and when - retained for exactly 90 days from <see cref="OccurredAt"/>. Deliberately
/// carries no stock details, prompt/content, secrets, SQL diagnostics, or raw payloads; its own
/// <see cref="Id"/> is an internal row identity never returned by any API.
/// </summary>
public sealed record AuditFact
{
    public const int RetentionDays = 90;

    public required Guid Id { get; init; }

    public required AuditEventType EventType { get; init; }

    public required AuditActorKind ActorKind { get; init; }

    /// <summary>The actor's identity: a Participant's <see cref="ParticipantId"/> string, or a Recovery Administrator's trusted claim value.</summary>
    public required string ActorId { get; init; }

    public required InventoryId InventoryId { get; init; }

    /// <summary>The Participant whose Membership/ownership changed, or null for events with no single affected Participant (for example a non-disclosing AccessDenied).</summary>
    public ParticipantId? SubjectParticipantId { get; init; }

    /// <summary>A short, coarse outcome/reason code (for example "Granted:Viewer", "Denied:NotAMember") - never free text, prompts, or diagnostic detail.</summary>
    public required string OutcomeCode { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public DateTimeOffset ExpiresAt => OccurredAt.AddDays(RetentionDays);

    public static AuditFact Create(
        AuditEventType eventType,
        AuditActorKind actorKind,
        string actorId,
        InventoryId inventoryId,
        ParticipantId? subjectParticipantId,
        string outcomeCode,
        DateTimeOffset occurredAt)
    {
        return new AuditFact
        {
            Id = Guid.NewGuid(),
            EventType = eventType,
            ActorKind = actorKind,
            ActorId = RequireNonBlank(actorId, nameof(actorId)),
            InventoryId = inventoryId,
            SubjectParticipantId = subjectParticipantId,
            OutcomeCode = RequireNonBlank(outcomeCode, nameof(outcomeCode)),
            OccurredAt = occurredAt,
        };
    }

    private static string RequireNonBlank(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value must not be blank.", parameterName);
        }

        return value.Trim();
    }
}
