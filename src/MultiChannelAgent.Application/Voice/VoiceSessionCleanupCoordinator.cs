using MultiChannelAgent.Domain.Voice;

namespace MultiChannelAgent.Application.Voice;

/// <summary>
/// Performs a single cleanup pass: finds expired/idle non-ended sessions and force-closes each,
/// then reclaims sessions owned by stale instances whose last heartbeat is older than the lease
/// threshold. Deduplicates overlapping candidates. Uses a bounded non-cancelled token for
/// mandatory cleanup work and surfaces failures rather than swallowing them. On CAS conflict,
/// re-reads and retries so that a concurrent Negotiating→Active transition still terminates
/// the actual provider session.
/// </summary>
public sealed class VoiceSessionCleanupCoordinator(
    IVoiceSessionStore store,
    IVoiceLiveGateway gateway,
    TimeProvider timeProvider,
    string ownerInstanceId,
    TimeSpan leaseTimeout)
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(10);
    private const int MaxCasRetries = 3;

    /// <summary>
    /// Finds expired/idle and stale-owner sessions, deduplicates, and force-closes each.
    /// Uses the caller token for queries and a bounded cleanup token for mutations.
    /// </summary>
    public async Task CleanupAsync(CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();

        var expiredOrIdle = await store.FindExpiredOrIdleAsync(now, ct);
        var heartbeatCutoff = now - leaseTimeout;
        var staleOwner = await store.FindStaleOwnerSessionsAsync(ownerInstanceId, heartbeatCutoff, ct);

        var seen = new HashSet<Guid>();
        var candidates = new List<VoiceSession>();
        foreach (var s in expiredOrIdle.Concat(staleOwner))
        {
            if (seen.Add(s.Id.Value))
                candidates.Add(s);
        }

        if (candidates.Count == 0)
            return;

        using var cleanupCts = new CancellationTokenSource(CleanupTimeout);
        var failures = new List<Exception>();

        foreach (var session in candidates)
        {
            await ForceCloseSessionAsync(session, now, failures, cleanupCts.Token);
        }

        if (failures.Count > 0)
            throw new AggregateException(failures);
    }

    private async Task ForceCloseSessionAsync(
        VoiceSession session, DateTimeOffset now, List<Exception> failures, CancellationToken ct)
    {
        if (session.Status == VoiceSessionStatus.Ended)
            return;

        var terminatedControlIds = new HashSet<string>();
        var current = session;

        for (int attempt = 0; attempt < MaxCasRetries; attempt++)
        {
            var expectedStatus = current.Status;
            var controlSessionId = current.ControlSessionId;
            current.End(now);

            if (controlSessionId is not null && terminatedControlIds.Add(controlSessionId))
            {
                try
                {
                    await gateway.TerminateAsync(controlSessionId, ct);
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            }

            bool persisted;
            try
            {
                persisted = await store.UpdateAsync(current, expectedStatus, ct);
            }
            catch (Exception ex)
            {
                failures.Add(ex);
                return;
            }

            if (persisted)
                return;

            // CAS conflict — re-read to observe latest state
            VoiceSession? reread;
            try
            {
                reread = await store.FindByIdAsync(session.Id, ct);
            }
            catch (Exception ex)
            {
                failures.Add(ex);
                return;
            }

            if (reread is null || reread.Status == VoiceSessionStatus.Ended)
                return;

            current = reread;
        }

        failures.Add(new InvalidOperationException(
            $"Voice session cleanup could not be persisted after {MaxCasRetries} attempts for session {session.Id.Value}."));
    }
}
