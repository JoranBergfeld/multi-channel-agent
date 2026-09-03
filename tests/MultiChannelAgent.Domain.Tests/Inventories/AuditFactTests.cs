using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Domain.Tests.Inventories;

public class AuditFactTests
{
    private static readonly InventoryId SomeInventory = new(Guid.Parse("44444444-4444-4444-4444-444444444444"));
    private static readonly ParticipantId SomeParticipant = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));

    [Fact]
    public void Create_sets_expiry_to_exactly_ninety_days_after_occurrence()
    {
        var occurredAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var fact = AuditFact.Create(
            AuditEventType.MembershipGranted,
            AuditActorKind.Participant,
            SomeParticipant.ToString(),
            SomeInventory,
            SomeParticipant,
            "Granted:Viewer",
            occurredAt);

        Assert.Equal(occurredAt.AddDays(90), fact.ExpiresAt);
        Assert.NotEqual(default, fact.Id);
        Assert.Equal(AuditEventType.MembershipGranted, fact.EventType);
        Assert.Equal(AuditActorKind.Participant, fact.ActorKind);
        Assert.Equal(SomeInventory, fact.InventoryId);
        Assert.Equal(SomeParticipant, fact.SubjectParticipantId);
        Assert.Equal("Granted:Viewer", fact.OutcomeCode);
    }

    [Fact]
    public void Create_allows_a_null_subject_for_events_with_no_affected_participant()
    {
        var fact = AuditFact.Create(
            AuditEventType.AccessDenied,
            AuditActorKind.Participant,
            SomeParticipant.ToString(),
            SomeInventory,
            subjectParticipantId: null,
            "Denied:NotAMember",
            DateTimeOffset.UtcNow);

        Assert.Null(fact.SubjectParticipantId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_a_blank_actor_id(string blank)
    {
        Assert.Throws<ArgumentException>(() => AuditFact.Create(
            AuditEventType.AccessDenied, AuditActorKind.Participant, blank, SomeInventory, null, "Denied:NotAMember", DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_a_blank_outcome_code(string blank)
    {
        Assert.Throws<ArgumentException>(() => AuditFact.Create(
            AuditEventType.AccessDenied, AuditActorKind.Participant, SomeParticipant.ToString(), SomeInventory, null, blank, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Create_generates_a_distinct_id_per_call()
    {
        var now = DateTimeOffset.UtcNow;
        var first = AuditFact.Create(AuditEventType.AccessDenied, AuditActorKind.Participant, "actor", SomeInventory, null, "Denied", now);
        var second = AuditFact.Create(AuditEventType.AccessDenied, AuditActorKind.Participant, "actor", SomeInventory, null, "Denied", now);

        Assert.NotEqual(first.Id, second.Id);
    }
}
