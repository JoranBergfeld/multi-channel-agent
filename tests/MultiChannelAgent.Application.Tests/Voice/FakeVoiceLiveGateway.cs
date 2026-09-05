using MultiChannelAgent.Application.Voice;

namespace MultiChannelAgent.Application.Tests.Voice;

/// <summary>
/// Deterministic in-process fake for <see cref="IVoiceLiveGateway"/> used in Application-layer tests.
/// Sessions are keyed by a generated control ID. Supports a configurable SDP answer template
/// and a one-shot negotiation failure for error-path tests.
/// </summary>
public sealed class FakeVoiceLiveGateway : IVoiceLiveGateway
{
    private readonly Lock gate = new();
    private readonly HashSet<string> activeSessions = [];
    private Exception? nextNegotiationFailure;
    private int negotiationCount;

    /// <summary>SDP answer text returned by every successful negotiation.</summary>
    public string SdpAnswerTemplate { get; set; } = "v=0\r\no=fake-server 0 0 IN IP4 127.0.0.1\r\n";

    /// <summary>Total number of <see cref="NegotiateAsync"/> invocations (including failures).</summary>
    public int NegotiationCount { get { lock (gate) { return negotiationCount; } } }

    /// <summary>Number of currently active (non-terminated) sessions.</summary>
    public int ActiveSessionCount { get { lock (gate) { return activeSessions.Count; } } }

    /// <summary>
    /// When set, the next call to <see cref="NegotiateAsync"/> throws this exception and clears
    /// the field so subsequent calls succeed.
    /// </summary>
    public Exception? NextNegotiationFailure
    {
        get { lock (gate) { return nextNegotiationFailure; } }
        set { lock (gate) { nextNegotiationFailure = value; } }
    }

    public Task<VoiceLiveNegotiationResult> NegotiateAsync(
        VoiceLiveNegotiationRequest request, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            negotiationCount++;

            if (nextNegotiationFailure is not null)
            {
                var failure = nextNegotiationFailure;
                nextNegotiationFailure = null;
                throw failure;
            }

            var controlSessionId = Guid.NewGuid().ToString("N");
            activeSessions.Add(controlSessionId);
            return Task.FromResult(new VoiceLiveNegotiationResult(controlSessionId, SdpAnswerTemplate));
        }
    }

    public Task TerminateAsync(string controlSessionId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            activeSessions.Remove(controlSessionId);
        }

        return Task.CompletedTask;
    }

    public bool OwnsSession(string controlSessionId)
    {
        lock (gate)
        {
            return activeSessions.Contains(controlSessionId);
        }
    }
}
