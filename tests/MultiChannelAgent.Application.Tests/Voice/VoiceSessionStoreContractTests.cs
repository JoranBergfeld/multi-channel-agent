using MultiChannelAgent.Application.Voice;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Domain.Voice;

namespace MultiChannelAgent.Application.Tests.Voice;

/// <summary>
/// Contract tests for any <see cref="IVoiceSessionStore"/> implementation. Both the in-memory test
/// double and the SQL-backed store must satisfy these invariants identically.
/// </summary>
public abstract class VoiceSessionStoreContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly ParticipantId ParticipantA = new(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
    private static readonly ParticipantId ParticipantB = new(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
    private static readonly ChannelConversationId SomeConversation = new("conv-1");
    private const string OwnerA = "instance-a";
    private const string OwnerB = "instance-b";
    private const int DefaultCap = 5;

    protected abstract IVoiceSessionStore CreateStore();

    private static VoiceSessionDeadlines DefaultDeadlines(DateTimeOffset at) => new(
        ExpiresAt: at + TimeSpan.FromMinutes(30),
        WarningAt: at + TimeSpan.FromMinutes(25),
        IdleExpiresAt: at + TimeSpan.FromSeconds(60));

    private static VoiceSession MakeSession(
        ParticipantId? participant = null,
        string owner = OwnerA,
        DateTimeOffset? at = null)
    {
        var t = at ?? Now;
        return VoiceSession.Reserve(participant ?? ParticipantA, SomeConversation, owner, t, DefaultDeadlines(t));
    }

    // ── First admission ──────────────────────────────────────────────────────

    [Fact]
    public async Task First_admission_succeeds()
    {
        var store = CreateStore();
        var session = MakeSession();

        var result = await store.TryAdmitAsync(session, DefaultCap, CancellationToken.None);

        Assert.True(result.Admitted);
        Assert.NotNull(result.Session);
        Assert.Equal(session.Id, result.Session.Id);
    }

    // ── Same participant Negotiating → AlreadyActive ─────────────────────────

    [Fact]
    public async Task Same_participant_negotiating_returns_AlreadyActive()
    {
        var store = CreateStore();
        var first = MakeSession();
        await store.TryAdmitAsync(first, DefaultCap, CancellationToken.None);

        var second = MakeSession();
        var result = await store.TryAdmitAsync(second, DefaultCap, CancellationToken.None);

        Assert.False(result.Admitted);
        Assert.Equal(VoiceAdmissionDenialReason.AlreadyActive, result.DenialReason);
    }

    [Fact]
    public async Task Same_participant_active_returns_AlreadyActive()
    {
        var store = CreateStore();
        var first = MakeSession();
        await store.TryAdmitAsync(first, DefaultCap, CancellationToken.None);

        // Activate the first session
        first.Activate("ctrl-1", Now);
        await store.UpdateAsync(first, VoiceSessionStatus.Negotiating, CancellationToken.None);

        var second = MakeSession();
        var result = await store.TryAdmitAsync(second, DefaultCap, CancellationToken.None);

        Assert.False(result.Admitted);
        Assert.Equal(VoiceAdmissionDenialReason.AlreadyActive, result.DenialReason);
    }

    // ── Cap distinct reason ──────────────────────────────────────────────────

    [Fact]
    public async Task Cap_reached_returns_GlobalCapReached()
    {
        var store = CreateStore();
        // Fill cap with distinct participants
        for (var i = 0; i < 2; i++)
        {
            var p = new ParticipantId(Guid.NewGuid());
            var s = MakeSession(participant: p);
            var r = await store.TryAdmitAsync(s, 2, CancellationToken.None);
            Assert.True(r.Admitted);
        }

        var overflow = MakeSession(participant: new ParticipantId(Guid.NewGuid()));
        var result = await store.TryAdmitAsync(overflow, 2, CancellationToken.None);

        Assert.False(result.Admitted);
        Assert.Equal(VoiceAdmissionDenialReason.GlobalCapReached, result.DenialReason);
    }

    // ── Negotiating counted in cap ───────────────────────────────────────────

    [Fact]
    public async Task Negotiating_sessions_count_toward_cap()
    {
        var store = CreateStore();
        // One negotiating session fills a cap of 1
        var s = MakeSession(participant: ParticipantA);
        await store.TryAdmitAsync(s, 1, CancellationToken.None);

        var overflow = MakeSession(participant: ParticipantB);
        var result = await store.TryAdmitAsync(overflow, 1, CancellationToken.None);

        Assert.False(result.Admitted);
        Assert.Equal(VoiceAdmissionDenialReason.GlobalCapReached, result.DenialReason);
    }

    // ── Ended frees slot ─────────────────────────────────────────────────────

    [Fact]
    public async Task Ended_session_frees_slot_for_same_participant()
    {
        var store = CreateStore();
        var first = MakeSession();
        await store.TryAdmitAsync(first, DefaultCap, CancellationToken.None);

        first.End(Now + TimeSpan.FromMinutes(1));
        await store.UpdateAsync(first, VoiceSessionStatus.Negotiating, CancellationToken.None);

        var second = MakeSession(at: Now + TimeSpan.FromMinutes(2));
        var result = await store.TryAdmitAsync(second, DefaultCap, CancellationToken.None);

        Assert.True(result.Admitted);
    }

    [Fact]
    public async Task Ended_session_frees_cap_slot()
    {
        var store = CreateStore();
        var first = MakeSession(participant: ParticipantA);
        await store.TryAdmitAsync(first, 1, CancellationToken.None);

        first.End(Now + TimeSpan.FromMinutes(1));
        await store.UpdateAsync(first, VoiceSessionStatus.Negotiating, CancellationToken.None);

        var second = MakeSession(participant: ParticipantB, at: Now + TimeSpan.FromMinutes(2));
        var result = await store.TryAdmitAsync(second, 1, CancellationToken.None);

        Assert.True(result.Admitted);
    }

    // ── Exact round trip ─────────────────────────────────────────────────────

    [Fact]
    public async Task Round_trip_preserves_all_fields()
    {
        var store = CreateStore();
        var session = MakeSession();
        await store.TryAdmitAsync(session, DefaultCap, CancellationToken.None);

        var loaded = await store.FindByIdAsync(session.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(session.Id, loaded.Id);
        Assert.Equal(session.ParticipantId, loaded.ParticipantId);
        Assert.Equal(session.ChannelConversationId, loaded.ChannelConversationId);
        Assert.Null(loaded.ControlSessionId);
        Assert.Equal(session.OwnerInstanceId, loaded.OwnerInstanceId);
        Assert.Equal(VoiceSessionStatus.Negotiating, loaded.Status);
        Assert.True(loaded.OccupiesSlot);
        Assert.Equal(session.StartedAt, loaded.StartedAt);
        Assert.Equal(session.LastHeartbeatAt, loaded.LastHeartbeatAt);
        Assert.Null(loaded.EndedAt);
        Assert.Equal(session.ExpiresAt, loaded.ExpiresAt);
        Assert.Equal(session.WarningAt, loaded.WarningAt);
        Assert.Equal(session.IdleExpiresAt, loaded.IdleExpiresAt);
        Assert.False(loaded.WarningIssued);
    }

    // ── Update heartbeat/lifecycle ───────────────────────────────────────────

    [Fact]
    public async Task Update_persists_heartbeat_and_idle_extension()
    {
        var store = CreateStore();
        var session = MakeSession();
        await store.TryAdmitAsync(session, DefaultCap, CancellationToken.None);

        session.Activate("ctrl-1", Now + TimeSpan.FromSeconds(1));
        await store.UpdateAsync(session, VoiceSessionStatus.Negotiating, CancellationToken.None);

        var heartbeatTime = Now + TimeSpan.FromSeconds(30);
        session.RecordHeartbeat(heartbeatTime, TimeSpan.FromSeconds(60));
        await store.UpdateAsync(session, VoiceSessionStatus.Active, CancellationToken.None);

        var loaded = await store.FindByIdAsync(session.Id, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal(VoiceSessionStatus.Active, loaded.Status);
        Assert.Equal(heartbeatTime, loaded.LastHeartbeatAt);
        Assert.Equal("ctrl-1", loaded.ControlSessionId);
    }

    [Fact]
    public async Task Update_with_wrong_expected_status_returns_false()
    {
        var store = CreateStore();
        var session = MakeSession();
        await store.TryAdmitAsync(session, DefaultCap, CancellationToken.None);

        // Try to update expecting Active, but session is Negotiating
        var updated = await store.UpdateAsync(session, VoiceSessionStatus.Active, CancellationToken.None);

        Assert.False(updated);
    }

    [Fact]
    public async Task Update_nonexistent_session_returns_false()
    {
        var store = CreateStore();
        var session = MakeSession();

        var updated = await store.UpdateAsync(session, VoiceSessionStatus.Negotiating, CancellationToken.None);

        Assert.False(updated);
    }

    // ── Expired/idle query ───────────────────────────────────────────────────

    [Fact]
    public async Task FindExpiredOrIdle_returns_expired_sessions()
    {
        var store = CreateStore();
        var session = MakeSession();
        await store.TryAdmitAsync(session, DefaultCap, CancellationToken.None);

        var afterExpiry = Now + TimeSpan.FromMinutes(31);
        var results = await store.FindExpiredOrIdleAsync(afterExpiry, CancellationToken.None);

        Assert.Single(results);
        Assert.Equal(session.Id, results[0].Id);
    }

    [Fact]
    public async Task FindExpiredOrIdle_returns_idle_active_sessions()
    {
        var store = CreateStore();
        var session = MakeSession();
        await store.TryAdmitAsync(session, DefaultCap, CancellationToken.None);

        session.Activate("ctrl-1", Now);
        await store.UpdateAsync(session, VoiceSessionStatus.Negotiating, CancellationToken.None);

        var afterIdle = Now + TimeSpan.FromSeconds(61);
        var results = await store.FindExpiredOrIdleAsync(afterIdle, CancellationToken.None);

        Assert.Single(results);
        Assert.Equal(session.Id, results[0].Id);
    }

    [Fact]
    public async Task FindExpiredOrIdle_excludes_ended_sessions()
    {
        var store = CreateStore();
        var session = MakeSession();
        await store.TryAdmitAsync(session, DefaultCap, CancellationToken.None);

        session.End(Now + TimeSpan.FromMinutes(1));
        await store.UpdateAsync(session, VoiceSessionStatus.Negotiating, CancellationToken.None);

        var afterExpiry = Now + TimeSpan.FromMinutes(31);
        var results = await store.FindExpiredOrIdleAsync(afterExpiry, CancellationToken.None);

        Assert.Empty(results);
    }

    // ── Stale-owner query ───────────────────────────────────────────────────

    [Fact]
    public async Task FindStaleOwnerSessions_returns_other_owner_past_cutoff()
    {
        var store = CreateStore();
        var session = MakeSession(participant: ParticipantB, owner: OwnerB);
        await store.TryAdmitAsync(session, DefaultCap, CancellationToken.None);

        // OwnerB heartbeat at Now; cutoff at Now + 1 minute → heartbeat is stale
        var results = await store.FindStaleOwnerSessionsAsync(
            OwnerA, Now + TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.Single(results);
        Assert.Equal(session.Id, results[0].Id);
    }

    [Fact]
    public async Task FindStaleOwnerSessions_excludes_current_owner()
    {
        var store = CreateStore();
        var session = MakeSession(owner: OwnerA);
        await store.TryAdmitAsync(session, DefaultCap, CancellationToken.None);

        var results = await store.FindStaleOwnerSessionsAsync(
            OwnerA, Now + TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public async Task FindStaleOwnerSessions_excludes_fresh_other_owner()
    {
        var store = CreateStore();
        var session = MakeSession(participant: ParticipantB, owner: OwnerB);
        await store.TryAdmitAsync(session, DefaultCap, CancellationToken.None);

        // OwnerB heartbeat at Now; cutoff at Now − 1 minute → heartbeat is fresh
        var results = await store.FindStaleOwnerSessionsAsync(
            OwnerA, Now - TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public async Task FindStaleOwnerSessions_excludes_ended_sessions()
    {
        var store = CreateStore();
        var session = MakeSession(participant: ParticipantB, owner: OwnerB);
        await store.TryAdmitAsync(session, DefaultCap, CancellationToken.None);

        session.End(Now + TimeSpan.FromMinutes(1));
        await store.UpdateAsync(session, VoiceSessionStatus.Negotiating, CancellationToken.None);

        var results = await store.FindStaleOwnerSessionsAsync(
            OwnerA, Now + TimeSpan.FromMinutes(2), CancellationToken.None);

        Assert.Empty(results);
    }

    // ── Cancellation ─────────────────────────────────────────────────────────

    [Fact]
    public async Task TryAdmitAsync_respects_cancellation()
    {
        var store = CreateStore();
        var session = MakeSession();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.TryAdmitAsync(session, DefaultCap, cts.Token));
    }

    [Fact]
    public async Task FindByIdAsync_respects_cancellation()
    {
        var store = CreateStore();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.FindByIdAsync(new VoiceSessionId(Guid.NewGuid()), cts.Token));
    }

    // ── Clone isolation ──────────────────────────────────────────────────────

    [Fact]
    public async Task Stored_session_is_isolated_from_caller_mutations()
    {
        var store = CreateStore();
        var session = MakeSession();
        await store.TryAdmitAsync(session, DefaultCap, CancellationToken.None);

        // Mutate the caller's reference
        session.End(Now + TimeSpan.FromMinutes(1));

        // The store should still have the original
        var loaded = await store.FindByIdAsync(session.Id, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal(VoiceSessionStatus.Negotiating, loaded.Status);
    }

    [Fact]
    public async Task Returned_session_is_isolated_from_store()
    {
        var store = CreateStore();
        var session = MakeSession();
        await store.TryAdmitAsync(session, DefaultCap, CancellationToken.None);

        var loaded = await store.FindByIdAsync(session.Id, CancellationToken.None);
        Assert.NotNull(loaded);
        loaded.End(Now + TimeSpan.FromMinutes(1));

        // The store should still have the original
        var reloaded = await store.FindByIdAsync(session.Id, CancellationToken.None);
        Assert.NotNull(reloaded);
        Assert.Equal(VoiceSessionStatus.Negotiating, reloaded.Status);
    }

    // ── Warning round trip ───────────────────────────────────────────────────

    [Fact]
    public async Task Round_trip_preserves_warning_issued()
    {
        var store = CreateStore();
        var session = MakeSession();
        await store.TryAdmitAsync(session, DefaultCap, CancellationToken.None);

        session.Activate("ctrl-1", Now);
        await store.UpdateAsync(session, VoiceSessionStatus.Negotiating, CancellationToken.None);

        var warned = session.ShouldWarn(Now + TimeSpan.FromMinutes(26));
        Assert.True(warned);
        await store.UpdateAsync(session, VoiceSessionStatus.Active, CancellationToken.None);

        var loaded = await store.FindByIdAsync(session.Id, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.True(loaded.WarningIssued);
    }
}
