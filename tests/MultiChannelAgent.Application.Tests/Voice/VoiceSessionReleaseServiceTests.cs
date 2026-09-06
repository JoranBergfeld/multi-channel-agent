using Microsoft.Extensions.Time.Testing;
using MultiChannelAgent.Application.Voice;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Domain.Voice;

namespace MultiChannelAgent.Application.Tests.Voice;

public sealed class VoiceSessionReleaseServiceTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _time = new(BaseTime);
    private readonly InMemoryVoiceSessionStore _store = new();
    private readonly FakeVoiceLiveGateway _gateway = new();

    private static readonly ParticipantId Alice = new(Guid.Parse("aaaa0000-0000-0000-0000-000000000001"));
    private static readonly ParticipantId Bob = new(Guid.Parse("bbbb0000-0000-0000-0000-000000000002"));
    private static readonly ChannelConversationId Conv1 = new("conv-1");
    private const string OwnerInstance = "test-instance";
    private static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(60);

    private static readonly VoiceSessionDeadlines DefaultDeadlines = new(
        ExpiresAt: BaseTime + TimeSpan.FromMinutes(30),
        WarningAt: BaseTime + TimeSpan.FromMinutes(25),
        IdleExpiresAt: BaseTime + TimeSpan.FromSeconds(60));

    private static readonly VoiceSessionDeadlines LongIdleDeadlines = new(
        ExpiresAt: BaseTime + TimeSpan.FromMinutes(30),
        WarningAt: BaseTime + TimeSpan.FromMinutes(25),
        IdleExpiresAt: BaseTime + TimeSpan.FromMinutes(30));

    private VoiceSessionReleaseService CreateService(TimeSpan? idleTimeout = null) =>
        new(_store, _gateway, _time, idleTimeout ?? DefaultIdleTimeout);

    private VoiceSessionReleaseService CreateService(IVoiceSessionStore store, TimeSpan? idleTimeout = null) =>
        new(store, _gateway, _time, idleTimeout ?? DefaultIdleTimeout);

    private async Task<VoiceSession> AdmitAndActivateAlice(VoiceSessionDeadlines? deadlines = null)
    {
        var d = deadlines ?? DefaultDeadlines;
        var s = VoiceSession.Reserve(Alice, Conv1, OwnerInstance, _time.GetUtcNow(), d);
        await _store.TryAdmitAsync(s, 5, CancellationToken.None);
        s.Activate("ctrl-1", _time.GetUtcNow());
        await _store.UpdateAsync(s, VoiceSessionStatus.Negotiating, CancellationToken.None);
        return s;
    }

    // ── Heartbeat: active + remaining seconds ────────────────────────────────

    [Fact]
    public async Task Heartbeat_active_returns_renewed_with_remaining_seconds()
    {
        var session = await AdmitAndActivateAlice();
        _time.Advance(TimeSpan.FromSeconds(30));

        var r = await CreateService().HeartbeatAsync(session.Id, Alice, CancellationToken.None);

        Assert.True(r.Renewed);
        Assert.Equal("active", r.LifecycleState);
        Assert.Equal(1770, r.RemainingSeconds); // 30 min − 30 s = 1770 s
        Assert.Null(r.ForcedCloseReason);
    }

    // ── Heartbeat: warning_due exactly once ──────────────────────────────────

    [Fact]
    public async Task Heartbeat_warning_due_exactly_once_and_persisted()
    {
        var session = await AdmitAndActivateAlice(LongIdleDeadlines);
        _time.Advance(TimeSpan.FromMinutes(25));

        var svc = CreateService(idleTimeout: TimeSpan.FromMinutes(30));
        var first = await svc.HeartbeatAsync(session.Id, Alice, CancellationToken.None);
        Assert.True(first.Renewed);
        Assert.Equal("warning_due", first.LifecycleState);

        _time.Advance(TimeSpan.FromSeconds(30));
        var second = await svc.HeartbeatAsync(session.Id, Alice, CancellationToken.None);
        Assert.True(second.Renewed);
        Assert.Equal("active", second.LifecycleState);

        // WarningIssued persisted
        var loaded = await _store.FindByIdAsync(session.Id, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.True(loaded.WarningIssued);
    }

    // ── Heartbeat: expired — authoritative no renewal ────────────────────────

    [Fact]
    public async Task Heartbeat_expired_not_renewed_with_forced_close_reason()
    {
        var session = await AdmitAndActivateAlice(LongIdleDeadlines);
        _time.Advance(TimeSpan.FromMinutes(31));

        var r = await CreateService().HeartbeatAsync(session.Id, Alice, CancellationToken.None);

        Assert.False(r.Renewed);
        Assert.Equal("expired", r.LifecycleState);
        Assert.Equal("expired", r.ForcedCloseReason);
        Assert.Null(r.RemainingSeconds);
    }

    // ── Heartbeat: idle — authoritative no renewal ───────────────────────────

    [Fact]
    public async Task Heartbeat_idle_not_renewed_with_forced_close_reason()
    {
        var session = await AdmitAndActivateAlice();
        _time.Advance(TimeSpan.FromSeconds(61));

        var r = await CreateService().HeartbeatAsync(session.Id, Alice, CancellationToken.None);

        Assert.False(r.Renewed);
        Assert.Equal("idle", r.LifecycleState);
        Assert.Equal("idle", r.ForcedCloseReason);
        Assert.Null(r.RemainingSeconds);
    }

    // ── Heartbeat: missing session ───────────────────────────────────────────

    [Fact]
    public async Task Heartbeat_missing_session_returns_not_found()
    {
        var r = await CreateService().HeartbeatAsync(
            new VoiceSessionId(Guid.NewGuid()), Alice, CancellationToken.None);

        Assert.False(r.Renewed);
        Assert.Equal("not_found", r.LifecycleState);
        Assert.Null(r.RemainingSeconds);
        Assert.Null(r.ForcedCloseReason);
    }

    // ── Heartbeat: wrong participant ─────────────────────────────────────────

    [Fact]
    public async Task Heartbeat_wrong_participant_returns_not_found()
    {
        var session = await AdmitAndActivateAlice();

        var r = await CreateService().HeartbeatAsync(session.Id, Bob, CancellationToken.None);

        Assert.False(r.Renewed);
        Assert.Equal("not_found", r.LifecycleState);
    }

    // ── Heartbeat: missing and wrong participant indistinguishable ────────────

    [Fact]
    public async Task Heartbeat_missing_and_wrong_participant_indistinguishable()
    {
        var session = await AdmitAndActivateAlice();
        var svc = CreateService();

        var missing = await svc.HeartbeatAsync(
            new VoiceSessionId(Guid.NewGuid()), Alice, CancellationToken.None);
        var wrongParticipant = await svc.HeartbeatAsync(session.Id, Bob, CancellationToken.None);

        Assert.Equal(missing.Renewed, wrongParticipant.Renewed);
        Assert.Equal(missing.LifecycleState, wrongParticipant.LifecycleState);
        Assert.Equal(missing.RemainingSeconds, wrongParticipant.RemainingSeconds);
        Assert.Equal(missing.ForcedCloseReason, wrongParticipant.ForcedCloseReason);
    }

    // ── Heartbeat: optimistic update conflict ────────────────────────────────

    [Fact]
    public async Task Heartbeat_update_conflict_does_not_report_renewed()
    {
        var session = await AdmitAndActivateAlice();
        _time.Advance(TimeSpan.FromSeconds(30));

        var conflictStore = new UpdateRejectingStore(_store);
        var svc = CreateService(conflictStore);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.HeartbeatAsync(session.Id, Alice, CancellationToken.None));
    }

    // ── Release: ends session and terminates gateway ─────────────────────────

    [Fact]
    public async Task Release_ends_session_and_terminates_gateway()
    {
        var session = await AdmitAndActivateAlice();

        await CreateService().ReleaseAsync(session.Id, Alice, CancellationToken.None);

        var found = await _store.FindByIdAsync(session.Id, CancellationToken.None);
        Assert.NotNull(found);
        Assert.Equal(VoiceSessionStatus.Ended, found.Status);
        Assert.False(found.OccupiesSlot);
        Assert.Equal(0, _gateway.ActiveSessionCount);
        Assert.False(_gateway.OwnsSession("ctrl-1"));
    }

    // ── Release: wrong participant leaves session untouched ───────────────────

    [Fact]
    public async Task Release_wrong_participant_leaves_session_untouched()
    {
        var session = await AdmitAndActivateAlice();

        await CreateService().ReleaseAsync(session.Id, Bob, CancellationToken.None);

        var found = await _store.FindByIdAsync(session.Id, CancellationToken.None);
        Assert.NotNull(found);
        Assert.Equal(VoiceSessionStatus.Active, found.Status);
        Assert.True(found.OccupiesSlot);
    }

    // ── Release: repeat is safe ──────────────────────────────────────────────

    [Fact]
    public async Task Release_repeat_is_safe()
    {
        var session = await AdmitAndActivateAlice();
        var svc = CreateService();

        await svc.ReleaseAsync(session.Id, Alice, CancellationToken.None);
        await svc.ReleaseAsync(session.Id, Alice, CancellationToken.None);

        var found = await _store.FindByIdAsync(session.Id, CancellationToken.None);
        Assert.NotNull(found);
        Assert.Equal(VoiceSessionStatus.Ended, found.Status);
    }

    // ── Release: no ControlSessionId skips termination ───────────────────────

    [Fact]
    public async Task Release_negotiating_session_without_control_id_skips_gateway_termination()
    {
        // Admit without activating — ControlSessionId is null
        var s = VoiceSession.Reserve(Alice, Conv1, OwnerInstance, _time.GetUtcNow(), DefaultDeadlines);
        await _store.TryAdmitAsync(s, 5, CancellationToken.None);

        await CreateService().ReleaseAsync(s.Id, Alice, CancellationToken.None);

        var found = await _store.FindByIdAsync(s.Id, CancellationToken.None);
        Assert.NotNull(found);
        Assert.Equal(VoiceSessionStatus.Ended, found.Status);
        Assert.Equal(0, _gateway.NegotiationCount);
    }

    // ── Release: cancellation after work begins still completes ──────────────

    [Fact]
    public async Task Release_uses_bounded_token_for_mandatory_cleanup()
    {
        var session = await AdmitAndActivateAlice();
        var cts = new CancellationTokenSource();
        var cancellingStore = new CancellingOnFindByIdStore(_store, cts);
        var svc = new VoiceSessionReleaseService(cancellingStore, _gateway, _time, DefaultIdleTimeout);

        await svc.ReleaseAsync(session.Id, Alice, cts.Token);

        var found = await _store.FindByIdAsync(session.Id, CancellationToken.None);
        Assert.NotNull(found);
        Assert.Equal(VoiceSessionStatus.Ended, found.Status);
        Assert.Equal(0, _gateway.ActiveSessionCount);
    }

    // ── Release: persistence failure surfaced ────────────────────────────────

    [Fact]
    public async Task Release_persistence_failure_is_surfaced()
    {
        var session = await AdmitAndActivateAlice();
        var failingStore = new UpdateThrowingStore(_store);
        var svc = new VoiceSessionReleaseService(failingStore, _gateway, _time, DefaultIdleTimeout);

        var ex = await Assert.ThrowsAsync<AggregateException>(
            () => svc.ReleaseAsync(session.Id, Alice, CancellationToken.None));

        Assert.Contains(ex.InnerExceptions, e => e.Message == "Store update failed");
    }

    // ── Test doubles ─────────────────────────────────────────────────────────

    private sealed class UpdateRejectingStore(IVoiceSessionStore inner) : IVoiceSessionStore
    {
        public Task<VoiceAdmissionResult> TryAdmitAsync(VoiceSession s, int cap, CancellationToken ct) =>
            inner.TryAdmitAsync(s, cap, ct);
        public Task<VoiceSession?> FindByIdAsync(VoiceSessionId id, CancellationToken ct) =>
            inner.FindByIdAsync(id, ct);
        public Task<bool> UpdateAsync(VoiceSession s, VoiceSessionStatus e, CancellationToken ct) =>
            Task.FromResult(false);
        public Task<IReadOnlyList<VoiceSession>> FindExpiredOrIdleAsync(DateTimeOffset now, CancellationToken ct) =>
            inner.FindExpiredOrIdleAsync(now, ct);
        public Task<IReadOnlyList<VoiceSession>> FindStaleOwnerSessionsAsync(
            string cur, DateTimeOffset cutoff, CancellationToken ct) =>
            inner.FindStaleOwnerSessionsAsync(cur, cutoff, ct);
    }

    private sealed class CancellingOnFindByIdStore(IVoiceSessionStore inner, CancellationTokenSource cts) : IVoiceSessionStore
    {
        public Task<VoiceAdmissionResult> TryAdmitAsync(VoiceSession s, int cap, CancellationToken ct) =>
            inner.TryAdmitAsync(s, cap, ct);
        public async Task<VoiceSession?> FindByIdAsync(VoiceSessionId id, CancellationToken ct)
        {
            var result = await inner.FindByIdAsync(id, ct);
            cts.Cancel();
            return result;
        }
        public Task<bool> UpdateAsync(VoiceSession s, VoiceSessionStatus e, CancellationToken ct) =>
            inner.UpdateAsync(s, e, ct);
        public Task<IReadOnlyList<VoiceSession>> FindExpiredOrIdleAsync(DateTimeOffset now, CancellationToken ct) =>
            inner.FindExpiredOrIdleAsync(now, ct);
        public Task<IReadOnlyList<VoiceSession>> FindStaleOwnerSessionsAsync(
            string cur, DateTimeOffset cutoff, CancellationToken ct) =>
            inner.FindStaleOwnerSessionsAsync(cur, cutoff, ct);
    }

    private sealed class UpdateThrowingStore(IVoiceSessionStore inner) : IVoiceSessionStore
    {
        public Task<VoiceAdmissionResult> TryAdmitAsync(VoiceSession s, int cap, CancellationToken ct) =>
            inner.TryAdmitAsync(s, cap, ct);
        public Task<VoiceSession?> FindByIdAsync(VoiceSessionId id, CancellationToken ct) =>
            inner.FindByIdAsync(id, ct);
        public Task<bool> UpdateAsync(VoiceSession s, VoiceSessionStatus e, CancellationToken ct) =>
            throw new InvalidOperationException("Store update failed");
        public Task<IReadOnlyList<VoiceSession>> FindExpiredOrIdleAsync(DateTimeOffset now, CancellationToken ct) =>
            inner.FindExpiredOrIdleAsync(now, ct);
        public Task<IReadOnlyList<VoiceSession>> FindStaleOwnerSessionsAsync(
            string cur, DateTimeOffset cutoff, CancellationToken ct) =>
            inner.FindStaleOwnerSessionsAsync(cur, cutoff, ct);
    }
}
