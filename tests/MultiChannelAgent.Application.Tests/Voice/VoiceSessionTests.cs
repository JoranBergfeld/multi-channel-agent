using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Voice;

namespace MultiChannelAgent.Application.Tests.Voice;

public sealed class VoiceSessionTests
{
    private static readonly ParticipantId SomeParticipant = new(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
    private const string SomeConversation = "conv-abc";
    private const string SomeOwner = "instance-1";
    private const string SomeControl = "ctrl-xyz";

    private static VoiceSessionDeadlines DefaultDeadlines(DateTimeOffset now) => new(
        ExpiresAt: now + TimeSpan.FromMinutes(30),
        WarningAt: now + TimeSpan.FromMinutes(25),
        IdleExpiresAt: now + TimeSpan.FromSeconds(60));

    private static VoiceSession ReservedSession(DateTimeOffset? at = null)
    {
        var t = at ?? Now;
        return VoiceSession.Reserve(SomeParticipant, SomeConversation, SomeOwner, t, DefaultDeadlines(t));
    }

    private static VoiceSession ActiveSession(DateTimeOffset? at = null)
    {
        var t = at ?? Now;
        var s = ReservedSession(t);
        s.Activate(SomeControl, t);
        return s;
    }

    // ── Reserve ──────────────────────────────────────────────────────────────

    [Fact]
    public void Reserve_returns_negotiating_session()
    {
        var session = ReservedSession();

        Assert.Equal(VoiceSessionStatus.Negotiating, session.Status);
    }

    [Fact]
    public void Reserve_occupies_slot()
    {
        var session = ReservedSession();

        Assert.True(session.OccupiesSlot);
    }

    [Fact]
    public void Reserve_generates_nonempty_id()
    {
        var session = ReservedSession();

        Assert.NotEqual(Guid.Empty, session.Id.Value);
    }

    [Fact]
    public void Reserve_sets_started_at_to_now()
    {
        var session = ReservedSession();

        Assert.Equal(Now, session.StartedAt);
    }

    [Fact]
    public void Reserve_sets_last_heartbeat_to_now()
    {
        var session = ReservedSession();

        Assert.Equal(Now, session.LastHeartbeatAt);
    }

    [Fact]
    public void Reserve_copies_deadlines_onto_session()
    {
        var deadlines = DefaultDeadlines(Now);
        var session = VoiceSession.Reserve(SomeParticipant, SomeConversation, SomeOwner, Now, deadlines);

        Assert.Equal(deadlines.ExpiresAt, session.ExpiresAt);
        Assert.Equal(deadlines.WarningAt, session.WarningAt);
        Assert.Equal(deadlines.IdleExpiresAt, session.IdleExpiresAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Reserve_throws_when_conversation_id_is_blank(string blank)
    {
        Assert.Throws<ArgumentException>(() =>
            VoiceSession.Reserve(SomeParticipant, blank, SomeOwner, Now, DefaultDeadlines(Now)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Reserve_throws_when_owner_instance_id_is_blank(string blank)
    {
        Assert.Throws<ArgumentException>(() =>
            VoiceSession.Reserve(SomeParticipant, SomeConversation, blank, Now, DefaultDeadlines(Now)));
    }

    [Fact]
    public void Reserve_throws_when_expires_at_is_in_the_past()
    {
        var deadlines = new VoiceSessionDeadlines(
            ExpiresAt: Now - TimeSpan.FromSeconds(1),
            WarningAt: Now - TimeSpan.FromMinutes(5),
            IdleExpiresAt: Now - TimeSpan.FromSeconds(30));

        Assert.Throws<ArgumentException>(() =>
            VoiceSession.Reserve(SomeParticipant, SomeConversation, SomeOwner, Now, deadlines));
    }

    [Fact]
    public void Reserve_throws_when_expires_at_equals_now()
    {
        var deadlines = new VoiceSessionDeadlines(
            ExpiresAt: Now,
            WarningAt: Now - TimeSpan.FromMinutes(5),
            IdleExpiresAt: Now - TimeSpan.FromSeconds(30));

        Assert.Throws<ArgumentException>(() =>
            VoiceSession.Reserve(SomeParticipant, SomeConversation, SomeOwner, Now, deadlines));
    }

    [Fact]
    public void Reserve_throws_when_warning_at_is_not_before_expires_at()
    {
        var deadlines = new VoiceSessionDeadlines(
            ExpiresAt: Now + TimeSpan.FromMinutes(30),
            WarningAt: Now + TimeSpan.FromMinutes(30),  // equal — invalid
            IdleExpiresAt: Now + TimeSpan.FromSeconds(60));

        Assert.Throws<ArgumentException>(() =>
            VoiceSession.Reserve(SomeParticipant, SomeConversation, SomeOwner, Now, deadlines));
    }

    [Fact]
    public void Reserve_throws_when_idle_expires_at_exceeds_expires_at()
    {
        var deadlines = new VoiceSessionDeadlines(
            ExpiresAt: Now + TimeSpan.FromMinutes(30),
            WarningAt: Now + TimeSpan.FromMinutes(25),
            IdleExpiresAt: Now + TimeSpan.FromMinutes(31));  // beyond expiry

        Assert.Throws<ArgumentException>(() =>
            VoiceSession.Reserve(SomeParticipant, SomeConversation, SomeOwner, Now, deadlines));
    }

    [Fact]
    public void Reserve_two_sessions_get_distinct_ids()
    {
        var a = ReservedSession();
        var b = ReservedSession();

        Assert.NotEqual(a.Id, b.Id);
    }

    // ── Activate ─────────────────────────────────────────────────────────────

    [Fact]
    public void Activate_transitions_to_active()
    {
        var session = ReservedSession();
        session.Activate(SomeControl, Now);

        Assert.Equal(VoiceSessionStatus.Active, session.Status);
    }

    [Fact]
    public void Activate_updates_heartbeat()
    {
        var session = ReservedSession();
        var later = Now.AddSeconds(5);
        session.Activate(SomeControl, later);

        Assert.Equal(later, session.LastHeartbeatAt);
    }

    [Fact]
    public void Activate_sets_control_session_id()
    {
        var session = ReservedSession();
        session.Activate(SomeControl, Now);

        Assert.Equal(SomeControl, session.ControlSessionId);
    }

    [Fact]
    public void Activate_keeps_slot_occupied()
    {
        var session = ReservedSession();
        session.Activate(SomeControl, Now);

        Assert.True(session.OccupiesSlot);
    }

    [Fact]
    public void Activate_throws_when_already_active()
    {
        var session = ActiveSession();

        Assert.Throws<InvalidOperationException>(() => session.Activate(SomeControl, Now));
    }

    [Fact]
    public void Activate_throws_when_ended()
    {
        var session = ReservedSession();
        session.Abandon(Now);

        Assert.Throws<InvalidOperationException>(() => session.Activate(SomeControl, Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Activate_throws_when_control_session_id_is_blank(string blank)
    {
        var session = ReservedSession();

        Assert.Throws<ArgumentException>(() => session.Activate(blank, Now));
    }

    [Fact]
    public void Activate_throws_when_now_is_before_started_at()
    {
        var session = ReservedSession();
        var before = Now - TimeSpan.FromSeconds(1);

        Assert.Throws<ArgumentException>(() => session.Activate(SomeControl, before));
    }

    // ── Abandon ──────────────────────────────────────────────────────────────

    [Fact]
    public void Abandon_transitions_to_ended()
    {
        var session = ReservedSession();
        session.Abandon(Now);

        Assert.Equal(VoiceSessionStatus.Ended, session.Status);
    }

    [Fact]
    public void Abandon_releases_slot()
    {
        var session = ReservedSession();
        session.Abandon(Now);

        Assert.False(session.OccupiesSlot);
    }

    [Fact]
    public void Abandon_sets_ended_at()
    {
        var session = ReservedSession();
        session.Abandon(Now);

        Assert.Equal(Now, session.EndedAt);
    }

    [Fact]
    public void Abandon_throws_when_active()
    {
        var session = ActiveSession();

        Assert.Throws<InvalidOperationException>(() => session.Abandon(Now));
    }

    [Fact]
    public void Abandon_throws_when_ended()
    {
        var session = ReservedSession();
        session.Abandon(Now);

        Assert.Throws<InvalidOperationException>(() => session.Abandon(Now));
    }

    // ── End ──────────────────────────────────────────────────────────────────

    [Fact]
    public void End_transitions_to_ended_from_active()
    {
        var session = ActiveSession();
        session.End(Now);

        Assert.Equal(VoiceSessionStatus.Ended, session.Status);
    }

    [Fact]
    public void End_transitions_to_ended_from_negotiating()
    {
        var session = ReservedSession();
        session.End(Now);

        Assert.Equal(VoiceSessionStatus.Ended, session.Status);
    }

    [Fact]
    public void End_releases_slot()
    {
        var session = ActiveSession();
        session.End(Now);

        Assert.False(session.OccupiesSlot);
    }

    [Fact]
    public void End_sets_ended_at()
    {
        var session = ActiveSession();
        session.End(Now);

        Assert.Equal(Now, session.EndedAt);
    }

    [Fact]
    public void End_is_idempotent_when_already_ended()
    {
        var session = ActiveSession();
        session.End(Now);
        var secondCall = () => session.End(Now.AddSeconds(10));  // should not throw

        var ex = Record.Exception(secondCall);
        Assert.Null(ex);
    }

    [Fact]
    public void End_retains_original_ended_at_when_called_again()
    {
        var session = ActiveSession();
        session.End(Now);
        session.End(Now.AddSeconds(10));

        Assert.Equal(Now, session.EndedAt);
    }

    // ── RecordHeartbeat ───────────────────────────────────────────────────────

    [Fact]
    public void RecordHeartbeat_updates_last_heartbeat_at()
    {
        var session = ActiveSession();
        var later = Now.AddSeconds(30);
        session.RecordHeartbeat(later, TimeSpan.FromSeconds(60));

        Assert.Equal(later, session.LastHeartbeatAt);
    }

    [Fact]
    public void RecordHeartbeat_updates_idle_expires_at_to_now_plus_timeout()
    {
        var session = ActiveSession();
        var later = Now.AddSeconds(30);
        session.RecordHeartbeat(later, TimeSpan.FromSeconds(60));

        Assert.Equal(later + TimeSpan.FromSeconds(60), session.IdleExpiresAt);
    }

    [Fact]
    public void RecordHeartbeat_clamps_idle_expires_at_to_expires_at()
    {
        var session = ActiveSession();
        // heartbeat close to expiry — now + timeout would exceed ExpiresAt
        var nearExpiry = Now + TimeSpan.FromMinutes(29);  // 1 min before ExpiresAt
        session.RecordHeartbeat(nearExpiry, TimeSpan.FromSeconds(120)); // 2-min timeout would exceed

        Assert.Equal(session.ExpiresAt, session.IdleExpiresAt);
    }

    [Fact]
    public void RecordHeartbeat_throws_when_not_active()
    {
        var session = ReservedSession();

        Assert.Throws<InvalidOperationException>(() =>
            session.RecordHeartbeat(Now, TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void RecordHeartbeat_throws_when_timeout_is_zero()
    {
        var session = ActiveSession();

        Assert.Throws<ArgumentException>(() =>
            session.RecordHeartbeat(Now, TimeSpan.Zero));
    }

    [Fact]
    public void RecordHeartbeat_throws_when_timeout_is_negative()
    {
        var session = ActiveSession();

        Assert.Throws<ArgumentException>(() =>
            session.RecordHeartbeat(Now, TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void RecordHeartbeat_throws_when_clock_goes_backward()
    {
        var session = ActiveSession();
        var before = Now - TimeSpan.FromSeconds(1);

        Assert.Throws<ArgumentException>(() =>
            session.RecordHeartbeat(before, TimeSpan.FromSeconds(60)));
    }

    // ── ShouldWarn ───────────────────────────────────────────────────────────

    [Fact]
    public void ShouldWarn_returns_false_before_warning_at()
    {
        var session = ActiveSession();
        var beforeWarning = session.WarningAt - TimeSpan.FromSeconds(1);

        Assert.False(session.ShouldWarn(beforeWarning));
    }

    [Fact]
    public void ShouldWarn_returns_true_at_warning_at()
    {
        var session = ActiveSession();

        Assert.True(session.ShouldWarn(session.WarningAt));
    }

    [Fact]
    public void ShouldWarn_returns_true_after_warning_at()
    {
        var session = ActiveSession();
        var afterWarning = session.WarningAt + TimeSpan.FromSeconds(1);

        Assert.True(session.ShouldWarn(afterWarning));
    }

    [Fact]
    public void ShouldWarn_sets_warning_issued()
    {
        var session = ActiveSession();
        session.ShouldWarn(session.WarningAt);

        Assert.True(session.WarningIssued);
    }

    [Fact]
    public void ShouldWarn_returns_false_after_warning_already_issued()
    {
        var session = ActiveSession();
        session.ShouldWarn(session.WarningAt);  // first call — issues warning

        Assert.False(session.ShouldWarn(session.WarningAt + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void ShouldWarn_returns_false_when_not_active_negotiating()
    {
        var session = ReservedSession();

        Assert.False(session.ShouldWarn(session.WarningAt));
    }

    [Fact]
    public void ShouldWarn_returns_false_when_not_active_ended()
    {
        var session = ActiveSession();
        session.End(Now);

        Assert.False(session.ShouldWarn(session.WarningAt));
    }

    [Fact]
    public void ShouldWarn_returns_false_when_expired()
    {
        var session = ActiveSession();
        var afterExpiry = session.ExpiresAt + TimeSpan.FromSeconds(1);

        Assert.False(session.ShouldWarn(afterExpiry));
    }

    // ── IsExpired ─────────────────────────────────────────────────────────────

    [Fact]
    public void IsExpired_returns_false_before_expires_at()
    {
        var session = ActiveSession();
        var before = session.ExpiresAt - TimeSpan.FromSeconds(1);

        Assert.False(session.IsExpired(before));
    }

    [Fact]
    public void IsExpired_returns_true_at_expires_at()
    {
        var session = ActiveSession();

        Assert.True(session.IsExpired(session.ExpiresAt));
    }

    [Fact]
    public void IsExpired_returns_true_after_expires_at()
    {
        var session = ActiveSession();
        var after = session.ExpiresAt + TimeSpan.FromSeconds(1);

        Assert.True(session.IsExpired(after));
    }

    // ── IsIdle ────────────────────────────────────────────────────────────────

    [Fact]
    public void IsIdle_returns_false_before_idle_expires_at()
    {
        var session = ActiveSession();
        var before = session.IdleExpiresAt - TimeSpan.FromSeconds(1);

        Assert.False(session.IsIdle(before));
    }

    [Fact]
    public void IsIdle_returns_true_at_idle_expires_at_when_active()
    {
        var session = ActiveSession();

        Assert.True(session.IsIdle(session.IdleExpiresAt));
    }

    [Fact]
    public void IsIdle_returns_false_when_negotiating()
    {
        var session = ReservedSession();

        Assert.False(session.IsIdle(session.IdleExpiresAt));
    }

    [Fact]
    public void IsIdle_returns_false_when_ended()
    {
        var session = ActiveSession();
        session.End(Now);

        Assert.False(session.IsIdle(session.IdleExpiresAt));
    }

    // ── Reconstitute ──────────────────────────────────────────────────────────

    [Fact]
    public void Reconstitute_roundtrips_an_active_session()
    {
        var id = new VoiceSessionId(Guid.NewGuid());
        var startedAt = Now;
        var expiresAt = Now + TimeSpan.FromMinutes(30);
        var warningAt = Now + TimeSpan.FromMinutes(25);
        var idleExpiresAt = Now + TimeSpan.FromSeconds(60);

        var session = VoiceSession.Reconstitute(
            id: id,
            participantId: SomeParticipant,
            channelConversationId: SomeConversation,
            controlSessionId: SomeControl,
            ownerInstanceId: SomeOwner,
            status: VoiceSessionStatus.Active,
            occupiesSlot: true,
            startedAt: startedAt,
            lastHeartbeatAt: startedAt,
            endedAt: null,
            expiresAt: expiresAt,
            warningAt: warningAt,
            idleExpiresAt: idleExpiresAt,
            warningIssued: false);

        Assert.Equal(id, session.Id);
        Assert.Equal(SomeParticipant, session.ParticipantId);
        Assert.Equal(SomeConversation, session.ChannelConversationId);
        Assert.Equal(SomeControl, session.ControlSessionId);
        Assert.Equal(SomeOwner, session.OwnerInstanceId);
        Assert.Equal(VoiceSessionStatus.Active, session.Status);
        Assert.True(session.OccupiesSlot);
        Assert.Equal(startedAt, session.StartedAt);
        Assert.Equal(startedAt, session.LastHeartbeatAt);
        Assert.Null(session.EndedAt);
        Assert.Equal(expiresAt, session.ExpiresAt);
        Assert.Equal(warningAt, session.WarningAt);
        Assert.Equal(idleExpiresAt, session.IdleExpiresAt);
        Assert.False(session.WarningIssued);
    }

    [Fact]
    public void Reconstitute_throws_when_id_is_empty()
    {
        Assert.Throws<ArgumentException>(() =>
            VoiceSession.Reconstitute(
                id: new VoiceSessionId(Guid.Empty),
                participantId: SomeParticipant,
                channelConversationId: SomeConversation,
                controlSessionId: SomeControl,
                ownerInstanceId: SomeOwner,
                status: VoiceSessionStatus.Active,
                occupiesSlot: true,
                startedAt: Now,
                lastHeartbeatAt: Now,
                endedAt: null,
                expiresAt: Now + TimeSpan.FromMinutes(30),
                warningAt: Now + TimeSpan.FromMinutes(25),
                idleExpiresAt: Now + TimeSpan.FromSeconds(60),
                warningIssued: false));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Reconstitute_throws_when_conversation_id_is_blank(string blank)
    {
        Assert.Throws<ArgumentException>(() =>
            VoiceSession.Reconstitute(
                id: new VoiceSessionId(Guid.NewGuid()),
                participantId: SomeParticipant,
                channelConversationId: blank,
                controlSessionId: SomeControl,
                ownerInstanceId: SomeOwner,
                status: VoiceSessionStatus.Active,
                occupiesSlot: true,
                startedAt: Now,
                lastHeartbeatAt: Now,
                endedAt: null,
                expiresAt: Now + TimeSpan.FromMinutes(30),
                warningAt: Now + TimeSpan.FromMinutes(25),
                idleExpiresAt: Now + TimeSpan.FromSeconds(60),
                warningIssued: false));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Reconstitute_throws_when_owner_instance_id_is_blank(string blank)
    {
        Assert.Throws<ArgumentException>(() =>
            VoiceSession.Reconstitute(
                id: new VoiceSessionId(Guid.NewGuid()),
                participantId: SomeParticipant,
                channelConversationId: SomeConversation,
                controlSessionId: SomeControl,
                ownerInstanceId: blank,
                status: VoiceSessionStatus.Active,
                occupiesSlot: true,
                startedAt: Now,
                lastHeartbeatAt: Now,
                endedAt: null,
                expiresAt: Now + TimeSpan.FromMinutes(30),
                warningAt: Now + TimeSpan.FromMinutes(25),
                idleExpiresAt: Now + TimeSpan.FromSeconds(60),
                warningIssued: false));
    }

    [Fact]
    public void Reconstitute_throws_when_active_without_control_session_id()
    {
        Assert.Throws<ArgumentException>(() =>
            VoiceSession.Reconstitute(
                id: new VoiceSessionId(Guid.NewGuid()),
                participantId: SomeParticipant,
                channelConversationId: SomeConversation,
                controlSessionId: null,
                ownerInstanceId: SomeOwner,
                status: VoiceSessionStatus.Active,
                occupiesSlot: true,
                startedAt: Now,
                lastHeartbeatAt: Now,
                endedAt: null,
                expiresAt: Now + TimeSpan.FromMinutes(30),
                warningAt: Now + TimeSpan.FromMinutes(25),
                idleExpiresAt: Now + TimeSpan.FromSeconds(60),
                warningIssued: false));
    }

    [Fact]
    public void Reconstitute_throws_when_ended_without_ended_at()
    {
        Assert.Throws<ArgumentException>(() =>
            VoiceSession.Reconstitute(
                id: new VoiceSessionId(Guid.NewGuid()),
                participantId: SomeParticipant,
                channelConversationId: SomeConversation,
                controlSessionId: null,
                ownerInstanceId: SomeOwner,
                status: VoiceSessionStatus.Ended,
                occupiesSlot: false,
                startedAt: Now,
                lastHeartbeatAt: Now,
                endedAt: null,
                expiresAt: Now + TimeSpan.FromMinutes(30),
                warningAt: Now + TimeSpan.FromMinutes(25),
                idleExpiresAt: Now + TimeSpan.FromSeconds(60),
                warningIssued: false));
    }

    [Fact]
    public void Reconstitute_throws_when_last_heartbeat_is_before_started_at()
    {
        Assert.Throws<ArgumentException>(() =>
            VoiceSession.Reconstitute(
                id: new VoiceSessionId(Guid.NewGuid()),
                participantId: SomeParticipant,
                channelConversationId: SomeConversation,
                controlSessionId: SomeControl,
                ownerInstanceId: SomeOwner,
                status: VoiceSessionStatus.Active,
                occupiesSlot: true,
                startedAt: Now,
                lastHeartbeatAt: Now - TimeSpan.FromSeconds(1),
                endedAt: null,
                expiresAt: Now + TimeSpan.FromMinutes(30),
                warningAt: Now + TimeSpan.FromMinutes(25),
                idleExpiresAt: Now + TimeSpan.FromSeconds(60),
                warningIssued: false));
    }
}
