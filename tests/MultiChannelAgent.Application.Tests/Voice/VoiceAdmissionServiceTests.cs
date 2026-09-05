using Microsoft.Extensions.Time.Testing;
using MultiChannelAgent.Application.Voice;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Domain.Voice;

namespace MultiChannelAgent.Application.Tests.Voice;

public sealed class VoiceAdmissionServiceTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryVoiceSessionStore _store = new();
    private readonly FakeVoiceLiveGateway _gateway = new();
    private static readonly ParticipantId Alice = new(Guid.Parse("aaaa0000-0000-0000-0000-000000000001"));
    private static readonly ParticipantId Bob = new(Guid.Parse("bbbb0000-0000-0000-0000-000000000002"));
    private static readonly ChannelConversationId Conv1 = new("conv-1");
    private static readonly ChannelConversationId Conv2 = new("conv-2");
    private const string SomeOffer = "v=0\r\no=caller 0\r\n";
    private const string OwnerInstance = "test-instance";

    private VoiceAdmissionService CreateService(int cap = 5) =>
        new(_store, _gateway, EnabledOptions(cap), _time, OwnerInstance);

    private static VoiceOptions EnabledOptions(int cap = 5) => new()
    {
        Enabled = true,
        Endpoint = "wss://test.services.ai.azure.com",
        Model = "gpt-4o-realtime",
        GlobalActiveCap = cap,
    };

    // ── Happy path ───────────────────────────────────────────────────────────

    [Fact]
    public async Task First_session_succeeds_and_contains_session_id_and_sdp_answer()
    {
        var result = await CreateService().AdmitAsync(Alice, Conv1, SomeOffer, CancellationToken.None);

        Assert.True(result.Admitted);
        Assert.NotNull(result.VoiceSessionId);
        Assert.NotEqual(default, result.VoiceSessionId.Value);
        Assert.Equal(_gateway.SdpAnswerTemplate, result.SdpAnswer);
    }

    // ── Denial paths ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Same_participant_denied_as_AlreadyActive_and_gateway_not_called_again()
    {
        var svc = CreateService();
        await svc.AdmitAsync(Alice, Conv1, SomeOffer, CancellationToken.None);

        var second = await svc.AdmitAsync(Alice, Conv2, SomeOffer, CancellationToken.None);

        Assert.False(second.Admitted);
        Assert.Equal(VoiceAdmissionDenialReason.AlreadyActive, second.DenialReason);
        Assert.Equal(1, _gateway.NegotiationCount);
    }

    [Fact]
    public async Task Global_cap_denied_as_GlobalCapReached_and_gateway_not_called()
    {
        var svc = CreateService(cap: 1);
        await svc.AdmitAsync(Alice, Conv1, SomeOffer, CancellationToken.None);

        var second = await svc.AdmitAsync(Bob, Conv2, SomeOffer, CancellationToken.None);

        Assert.False(second.Admitted);
        Assert.Equal(VoiceAdmissionDenialReason.GlobalCapReached, second.DenialReason);
        Assert.Equal(1, _gateway.NegotiationCount);
    }

    [Fact]
    public async Task Disabled_denied_as_VoiceDisabled_and_no_store_gateway_work()
    {
        var svc = new VoiceAdmissionService(
            _store, _gateway, new VoiceOptions { Enabled = false }, _time, OwnerInstance);

        var result = await svc.AdmitAsync(Alice, Conv1, SomeOffer, CancellationToken.None);

        Assert.False(result.Admitted);
        Assert.Equal(VoiceAdmissionDenialReason.VoiceDisabled, result.DenialReason);
        Assert.Equal(0, _gateway.NegotiationCount);

        // Verify no store work: same participant can be admitted when enabled.
        var enabledResult = await CreateService().AdmitAsync(Alice, Conv1, SomeOffer, CancellationToken.None);
        Assert.True(enabledResult.Admitted);
    }

    // ── Negotiation failure ──────────────────────────────────────────────────

    [Fact]
    public async Task Gateway_failure_abandons_reservation_and_retry_can_succeed()
    {
        _gateway.NextNegotiationFailure = new InvalidOperationException("Azure down");
        var svc = CreateService();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AdmitAsync(Alice, Conv1, SomeOffer, CancellationToken.None));

        var retry = await svc.AdmitAsync(Alice, Conv1, SomeOffer, CancellationToken.None);
        Assert.True(retry.Admitted);
    }

    // ── Activation failure (UpdateAsync false) ───────────────────────────────

    [Fact]
    public async Task Gateway_success_then_update_false_terminates_abandons_and_retry_succeeds()
    {
        var rejectingStore = new ActivationRejectingStore(_store);
        var svc = new VoiceAdmissionService(
            rejectingStore, _gateway, EnabledOptions(), _time, OwnerInstance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AdmitAsync(Alice, Conv1, SomeOffer, CancellationToken.None));
        Assert.Contains("conflict", ex.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(0, _gateway.ActiveSessionCount);

        var retry = await svc.AdmitAsync(Alice, Conv1, SomeOffer, CancellationToken.None);
        Assert.True(retry.Admitted);
    }

    // ── Activation failure (UpdateAsync throws) ──────────────────────────────

    [Fact]
    public async Task Gateway_success_then_activation_exception_preserves_original_and_cleans_up()
    {
        var throwingStore = new ActivationThrowingStore(_store);
        var svc = new VoiceAdmissionService(
            throwingStore, _gateway, EnabledOptions(), _time, OwnerInstance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AdmitAsync(Alice, Conv1, SomeOffer, CancellationToken.None));
        Assert.Equal("Simulated activation persistence failure.", ex.Message);

        Assert.Equal(0, _gateway.ActiveSessionCount);

        var retry = await svc.AdmitAsync(Alice, Conv1, SomeOffer, CancellationToken.None);
        Assert.True(retry.Admitted);
    }

    // ── Activation domain failure ────────────────────────────────────────────

    [Fact]
    public async Task Activation_domain_failure_terminates_provider_session_and_releases_reservation()
    {
        var observableGateway = new BlankControlIdGateway();
        var svc = new VoiceAdmissionService(
            _store, observableGateway, EnabledOptions(), _time, OwnerInstance);

        // Activate rejects blank ControlSessionId — original exception preserved.
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => svc.AdmitAsync(Alice, Conv1, SomeOffer, CancellationToken.None));
        Assert.Contains("Control session ID", ex.Message);

        // Provider session was cleaned up with the exact identifier returned by NegotiateAsync.
        Assert.True(observableGateway.TerminationRequested);
        Assert.Equal("", observableGateway.TerminatedControlSessionId);

        // Reservation was released — same participant can retry with a working gateway.
        var retrySvc = CreateService();
        var retry = await retrySvc.AdmitAsync(Alice, Conv1, SomeOffer, CancellationToken.None);
        Assert.True(retry.Admitted);
    }

    // ── Cancellation ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Cancellation_during_negotiation_still_reclaims_reservation()
    {
        var cts = new CancellationTokenSource();
        var cancellingGateway = new CancellingOnNegotiateGateway(cts);
        var svc = new VoiceAdmissionService(
            _store, cancellingGateway, EnabledOptions(), _time, OwnerInstance);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => svc.AdmitAsync(Alice, Conv1, SomeOffer, cts.Token));

        // Capacity was reclaimed despite the caller's token being cancelled.
        var retrySvc = CreateService();
        var retry = await retrySvc.AdmitAsync(Alice, Conv1, SomeOffer, CancellationToken.None);
        Assert.True(retry.Admitted);
    }

    // ── Cleanup failure ──────────────────────────────────────────────────────

    [Fact]
    public async Task Cleanup_failure_is_surfaced_not_swallowed()
    {
        var failingCleanupStore = new CleanupFailingStore(_store);
        _gateway.NextNegotiationFailure = new InvalidOperationException("Primary failure");
        var svc = new VoiceAdmissionService(
            failingCleanupStore, _gateway, EnabledOptions(), _time, OwnerInstance);

        var ex = await Assert.ThrowsAsync<AggregateException>(
            () => svc.AdmitAsync(Alice, Conv1, SomeOffer, CancellationToken.None));

        Assert.Contains(ex.InnerExceptions, e => e.Message == "Primary failure");
        Assert.Contains(ex.InnerExceptions, e => e.Message == "Cleanup store failure");
    }

    // ── Control boundary ─────────────────────────────────────────────────────

    [Fact]
    public async Task Returned_result_does_not_expose_ControlSessionId()
    {
        var result = await CreateService().AdmitAsync(Alice, Conv1, SomeOffer, CancellationToken.None);

        Assert.True(result.Admitted);
        var properties = typeof(VoiceConnectionAdmissionResult).GetProperties();
        Assert.DoesNotContain(properties, p => p.Name == "ControlSessionId");
    }

    // ── Test doubles ─────────────────────────────────────────────────────────

    /// <summary>
    /// Rejects the first activation update (session Active, expected Negotiating) then
    /// passes all subsequent calls through to the underlying store.
    /// </summary>
    private sealed class ActivationRejectingStore(IVoiceSessionStore inner) : IVoiceSessionStore
    {
        private bool _rejectActivation = true;

        public Task<VoiceAdmissionResult> TryAdmitAsync(VoiceSession session, int globalCap, CancellationToken ct) =>
            inner.TryAdmitAsync(session, globalCap, ct);

        public Task<VoiceSession?> FindByIdAsync(VoiceSessionId id, CancellationToken ct) =>
            inner.FindByIdAsync(id, ct);

        public Task<bool> UpdateAsync(VoiceSession session, VoiceSessionStatus expectedStatus, CancellationToken ct)
        {
            if (_rejectActivation
                && session.Status == VoiceSessionStatus.Active
                && expectedStatus == VoiceSessionStatus.Negotiating)
            {
                _rejectActivation = false;
                return Task.FromResult(false);
            }

            return inner.UpdateAsync(session, expectedStatus, ct);
        }

        public Task<IReadOnlyList<VoiceSession>> FindExpiredOrIdleAsync(DateTimeOffset now, CancellationToken ct) =>
            inner.FindExpiredOrIdleAsync(now, ct);

        public Task<IReadOnlyList<VoiceSession>> FindByOwnerInstanceAsync(string ownerInstanceId, CancellationToken ct) =>
            inner.FindByOwnerInstanceAsync(ownerInstanceId, ct);
    }

    /// <summary>
    /// Throws on the first activation update then passes subsequent calls through.
    /// </summary>
    private sealed class ActivationThrowingStore(IVoiceSessionStore inner) : IVoiceSessionStore
    {
        private bool _throwOnActivation = true;

        public Task<VoiceAdmissionResult> TryAdmitAsync(VoiceSession session, int globalCap, CancellationToken ct) =>
            inner.TryAdmitAsync(session, globalCap, ct);

        public Task<VoiceSession?> FindByIdAsync(VoiceSessionId id, CancellationToken ct) =>
            inner.FindByIdAsync(id, ct);

        public Task<bool> UpdateAsync(VoiceSession session, VoiceSessionStatus expectedStatus, CancellationToken ct)
        {
            if (_throwOnActivation
                && session.Status == VoiceSessionStatus.Active
                && expectedStatus == VoiceSessionStatus.Negotiating)
            {
                _throwOnActivation = false;
                throw new InvalidOperationException("Simulated activation persistence failure.");
            }

            return inner.UpdateAsync(session, expectedStatus, ct);
        }

        public Task<IReadOnlyList<VoiceSession>> FindExpiredOrIdleAsync(DateTimeOffset now, CancellationToken ct) =>
            inner.FindExpiredOrIdleAsync(now, ct);

        public Task<IReadOnlyList<VoiceSession>> FindByOwnerInstanceAsync(string ownerInstanceId, CancellationToken ct) =>
            inner.FindByOwnerInstanceAsync(ownerInstanceId, ct);
    }

    /// <summary>
    /// Cancels the provided <see cref="CancellationTokenSource"/> during negotiation to simulate
    /// mid-flight caller cancellation.
    /// </summary>
    private sealed class CancellingOnNegotiateGateway(CancellationTokenSource cts) : IVoiceLiveGateway
    {
        public Task<VoiceLiveNegotiationResult> NegotiateAsync(
            VoiceLiveNegotiationRequest request, CancellationToken cancellationToken)
        {
            cts.Cancel();
            throw new OperationCanceledException(cancellationToken);
        }

        public Task TerminateAsync(string controlSessionId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public bool OwnsSession(string controlSessionId) => false;
    }

    /// <summary>
    /// Delegates admission to the inner store but throws on any <see cref="UpdateAsync"/> call,
    /// simulating a cleanup persistence failure.
    /// </summary>
    private sealed class CleanupFailingStore(IVoiceSessionStore inner) : IVoiceSessionStore
    {
        public Task<VoiceAdmissionResult> TryAdmitAsync(VoiceSession session, int globalCap, CancellationToken ct) =>
            inner.TryAdmitAsync(session, globalCap, ct);

        public Task<VoiceSession?> FindByIdAsync(VoiceSessionId id, CancellationToken ct) =>
            inner.FindByIdAsync(id, ct);

        public Task<bool> UpdateAsync(VoiceSession session, VoiceSessionStatus expectedStatus, CancellationToken ct) =>
            throw new InvalidOperationException("Cleanup store failure");

        public Task<IReadOnlyList<VoiceSession>> FindExpiredOrIdleAsync(DateTimeOffset now, CancellationToken ct) =>
            inner.FindExpiredOrIdleAsync(now, ct);

        public Task<IReadOnlyList<VoiceSession>> FindByOwnerInstanceAsync(string ownerInstanceId, CancellationToken ct) =>
            inner.FindByOwnerInstanceAsync(ownerInstanceId, ct);
    }

    /// <summary>
    /// Observable gateway that returns a successful negotiation with a blank ControlSessionId,
    /// triggering an <see cref="VoiceSession.Activate"/> domain validation failure. Records
    /// whether <see cref="TerminateAsync"/> was called and the exact identifier it received.
    /// </summary>
    private sealed class BlankControlIdGateway : IVoiceLiveGateway
    {
        public bool TerminationRequested { get; private set; }
        public string? TerminatedControlSessionId { get; private set; }

        public Task<VoiceLiveNegotiationResult> NegotiateAsync(
            VoiceLiveNegotiationRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new VoiceLiveNegotiationResult("", "v=0\r\no=answer\r\n"));
        }

        public Task TerminateAsync(string controlSessionId, CancellationToken cancellationToken)
        {
            TerminationRequested = true;
            TerminatedControlSessionId = controlSessionId;
            return Task.CompletedTask;
        }

        public bool OwnsSession(string controlSessionId) => false;
    }
}
