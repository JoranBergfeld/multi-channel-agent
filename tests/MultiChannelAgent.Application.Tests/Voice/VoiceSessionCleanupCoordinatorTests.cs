using Microsoft.Extensions.Time.Testing;
using MultiChannelAgent.Application.Voice;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Domain.Voice;

namespace MultiChannelAgent.Application.Tests.Voice;

public sealed class VoiceSessionCleanupCoordinatorTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _time = new(BaseTime);
    private readonly InMemoryVoiceSessionStore _store = new();
    private readonly FakeVoiceLiveGateway _gateway = new();

    private static readonly ParticipantId Alice = new(Guid.Parse("aaaa0000-0000-0000-0000-000000000001"));
    private static readonly ParticipantId Bob = new(Guid.Parse("bbbb0000-0000-0000-0000-000000000002"));
    private static readonly ParticipantId Charlie = new(Guid.Parse("cccc0000-0000-0000-0000-000000000003"));
    private static readonly ChannelConversationId Conv1 = new("conv-1");
    private static readonly ChannelConversationId Conv2 = new("conv-2");
    private static readonly ChannelConversationId Conv3 = new("conv-3");
    private const string CurrentInstance = "current-instance";
    private const string DeadInstance = "dead-instance";
    private const string OtherInstance = "other-instance";
    private static readonly TimeSpan LeaseTimeout = TimeSpan.FromSeconds(60);

    private static readonly VoiceSessionDeadlines DefaultDeadlines = new(
        ExpiresAt: BaseTime + TimeSpan.FromMinutes(30),
        WarningAt: BaseTime + TimeSpan.FromMinutes(25),
        IdleExpiresAt: BaseTime + TimeSpan.FromSeconds(60));

    private static readonly VoiceSessionDeadlines LongIdleDeadlines = new(
        ExpiresAt: BaseTime + TimeSpan.FromMinutes(30),
        WarningAt: BaseTime + TimeSpan.FromMinutes(25),
        IdleExpiresAt: BaseTime + TimeSpan.FromMinutes(30));

    private VoiceSessionCleanupCoordinator CreateCoordinator(
        IVoiceSessionStore? store = null,
        IVoiceLiveGateway? gateway = null) =>
        new(store ?? _store, gateway ?? _gateway, _time, CurrentInstance, LeaseTimeout);

    private async Task<VoiceSession> AdmitAndActivate(
        ParticipantId participant, ChannelConversationId conv,
        string owner, string controlId, VoiceSessionDeadlines? deadlines = null)
    {
        var d = deadlines ?? DefaultDeadlines;
        var s = VoiceSession.Reserve(participant, conv, owner, _time.GetUtcNow(), d);
        await _store.TryAdmitAsync(s, 10, CancellationToken.None);
        s.Activate(controlId, _time.GetUtcNow());
        await _store.UpdateAsync(s, VoiceSessionStatus.Negotiating, CancellationToken.None);
        return s;
    }

    // ── Cleanup: closes expired sessions ─────────────────────────────────────

    [Fact]
    public async Task Cleanup_closes_expired_sessions()
    {
        var session = await AdmitAndActivate(Alice, Conv1, CurrentInstance, "ctrl-1", LongIdleDeadlines);
        _time.Advance(TimeSpan.FromMinutes(31));

        await CreateCoordinator().CleanupAsync(CancellationToken.None);

        var found = await _store.FindByIdAsync(session.Id, CancellationToken.None);
        Assert.NotNull(found);
        Assert.Equal(VoiceSessionStatus.Ended, found.Status);
        Assert.False(found.OccupiesSlot);
        Assert.Equal(0, _gateway.ActiveSessionCount);
    }

    // ── Cleanup: closes idle sessions ────────────────────────────────────────

    [Fact]
    public async Task Cleanup_closes_idle_sessions()
    {
        var session = await AdmitAndActivate(Alice, Conv1, CurrentInstance, "ctrl-1");
        _time.Advance(TimeSpan.FromSeconds(61));

        await CreateCoordinator().CleanupAsync(CancellationToken.None);

        var found = await _store.FindByIdAsync(session.Id, CancellationToken.None);
        Assert.NotNull(found);
        Assert.Equal(VoiceSessionStatus.Ended, found.Status);
        Assert.False(found.OccupiesSlot);
    }

    // ── Cleanup: stale other-owner past lease ────────────────────────────────

    [Fact]
    public async Task Cleanup_closes_stale_other_owner_session_past_lease()
    {
        var session = await AdmitAndActivate(Alice, Conv1, DeadInstance, "ctrl-1", LongIdleDeadlines);
        _time.Advance(TimeSpan.FromSeconds(61)); // Past lease timeout

        await CreateCoordinator().CleanupAsync(CancellationToken.None);

        var found = await _store.FindByIdAsync(session.Id, CancellationToken.None);
        Assert.NotNull(found);
        Assert.Equal(VoiceSessionStatus.Ended, found.Status);
        Assert.False(found.OccupiesSlot);
    }

    // ── Cleanup: skips current owner non-expired ─────────────────────────────

    [Fact]
    public async Task Cleanup_skips_current_owner_non_expired_session()
    {
        var session = await AdmitAndActivate(Alice, Conv1, CurrentInstance, "ctrl-1");
        _time.Advance(TimeSpan.FromSeconds(30)); // Not expired, not idle

        await CreateCoordinator().CleanupAsync(CancellationToken.None);

        var found = await _store.FindByIdAsync(session.Id, CancellationToken.None);
        Assert.NotNull(found);
        Assert.Equal(VoiceSessionStatus.Active, found.Status);
        Assert.True(found.OccupiesSlot);
    }

    // ── Cleanup: skips fresh other owner ─────────────────────────────────────

    [Fact]
    public async Task Cleanup_skips_fresh_other_owner_session()
    {
        var session = await AdmitAndActivate(Alice, Conv1, OtherInstance, "ctrl-1", LongIdleDeadlines);
        _time.Advance(TimeSpan.FromSeconds(30)); // Within lease timeout, not expired

        await CreateCoordinator().CleanupAsync(CancellationToken.None);

        var found = await _store.FindByIdAsync(session.Id, CancellationToken.None);
        Assert.NotNull(found);
        Assert.Equal(VoiceSessionStatus.Active, found.Status);
        Assert.True(found.OccupiesSlot);
    }

    // ── Cleanup: overlapping expired + stale closed once ─────────────────────

    [Fact]
    public async Task Cleanup_deduplicates_overlapping_expired_and_stale()
    {
        // Session is both expired AND stale (dead owner + past expiry)
        var session = await AdmitAndActivate(Alice, Conv1, DeadInstance, "ctrl-1", LongIdleDeadlines);
        _time.Advance(TimeSpan.FromMinutes(31)); // Past both expiry and lease

        await CreateCoordinator().CleanupAsync(CancellationToken.None);

        var found = await _store.FindByIdAsync(session.Id, CancellationToken.None);
        Assert.NotNull(found);
        Assert.Equal(VoiceSessionStatus.Ended, found.Status);
        Assert.False(found.OccupiesSlot);
        Assert.Equal(0, _gateway.ActiveSessionCount);
    }

    // ── Cancellation after lifecycle work does not prevent cleanup ────────────

    [Fact]
    public async Task Cancellation_after_find_does_not_prevent_mandatory_cleanup()
    {
        var session = await AdmitAndActivate(Alice, Conv1, CurrentInstance, "ctrl-1", LongIdleDeadlines);
        _time.Advance(TimeSpan.FromMinutes(31));

        var cts = new CancellationTokenSource();
        var cancellingStore = new CancellingAfterFindStore(_store, cts);
        var coordinator = new VoiceSessionCleanupCoordinator(
            cancellingStore, _gateway, _time, CurrentInstance, LeaseTimeout);

        await coordinator.CleanupAsync(cts.Token);

        var found = await _store.FindByIdAsync(session.Id, CancellationToken.None);
        Assert.NotNull(found);
        Assert.Equal(VoiceSessionStatus.Ended, found.Status);
    }

    // ── Gateway cleanup failure surfaced ─────────────────────────────────────

    [Fact]
    public async Task Cleanup_gateway_failure_is_surfaced()
    {
        var session = await AdmitAndActivate(Alice, Conv1, CurrentInstance, "ctrl-1", LongIdleDeadlines);
        _time.Advance(TimeSpan.FromMinutes(31));

        var failingGateway = new FailingTerminateGateway();
        var coordinator = CreateCoordinator(gateway: failingGateway);

        var ex = await Assert.ThrowsAsync<AggregateException>(
            () => coordinator.CleanupAsync(CancellationToken.None));
        Assert.Contains(ex.InnerExceptions, e => e.Message == "Gateway termination failed");
    }

    // ── Persistence cleanup failure surfaced ─────────────────────────────────

    [Fact]
    public async Task Cleanup_persistence_failure_is_surfaced()
    {
        var session = await AdmitAndActivate(Alice, Conv1, CurrentInstance, "ctrl-1", LongIdleDeadlines);
        _time.Advance(TimeSpan.FromMinutes(31));

        var failingStore = new UpdateThrowingOnCleanupStore(_store);
        var coordinator = CreateCoordinator(store: failingStore);

        var ex = await Assert.ThrowsAsync<AggregateException>(
            () => coordinator.CleanupAsync(CancellationToken.None));
        Assert.Contains(ex.InnerExceptions, e => e.Message == "Store update failed");
    }

    // ── Test doubles ─────────────────────────────────────────────────────────

    private sealed class CancellingAfterFindStore(IVoiceSessionStore inner, CancellationTokenSource cts) : IVoiceSessionStore
    {
        public Task<VoiceAdmissionResult> TryAdmitAsync(VoiceSession s, int cap, CancellationToken ct) =>
            inner.TryAdmitAsync(s, cap, ct);
        public Task<VoiceSession?> FindByIdAsync(VoiceSessionId id, CancellationToken ct) =>
            inner.FindByIdAsync(id, ct);
        public Task<bool> UpdateAsync(VoiceSession s, VoiceSessionStatus e, CancellationToken ct) =>
            inner.UpdateAsync(s, e, ct);
        public Task<IReadOnlyList<VoiceSession>> FindExpiredOrIdleAsync(DateTimeOffset now, CancellationToken ct) =>
            inner.FindExpiredOrIdleAsync(now, ct);
        public async Task<IReadOnlyList<VoiceSession>> FindStaleOwnerSessionsAsync(
            string cur, DateTimeOffset cutoff, CancellationToken ct)
        {
            var result = await inner.FindStaleOwnerSessionsAsync(cur, cutoff, ct);
            cts.Cancel();
            return result;
        }
    }

    private sealed class FailingTerminateGateway : IVoiceLiveGateway
    {
        public Task<VoiceLiveNegotiationResult> NegotiateAsync(
            VoiceLiveNegotiationRequest request, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task TerminateAsync(string controlSessionId, CancellationToken ct) =>
            throw new InvalidOperationException("Gateway termination failed");
        public bool OwnsSession(string controlSessionId) => false;
    }

    private sealed class UpdateThrowingOnCleanupStore(IVoiceSessionStore inner) : IVoiceSessionStore
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
