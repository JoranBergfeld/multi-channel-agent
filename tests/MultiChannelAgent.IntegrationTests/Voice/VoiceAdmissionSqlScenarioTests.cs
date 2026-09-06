using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using MultiChannelAgent.Application.Tests.Voice;
using MultiChannelAgent.Application.Voice;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Domain.Voice;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Voice;

namespace MultiChannelAgent.IntegrationTests.Voice;

/// <summary>
/// SQL Server-backed admission concurrency scenarios proving that <see cref="SqlVoiceSessionStore"/>
/// serialization holds under real range-lock contention. Each test gets its own ephemeral database
/// (via <see cref="SqlUserDatabase"/>) so scenarios have no order dependence and the shared container
/// is never polluted. Docker gating follows <see cref="DockerTestPolicy"/>: absent Docker skips
/// locally, fails in CI where <c>REQUIRE_DOCKER_TESTS=true</c>.
/// </summary>
public sealed class VoiceAdmissionSqlScenarioTests : SqlIntegrationTestBase
{
    private static readonly DateTimeOffset Epoch = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
    private const string OwnerInstance = "scenario-instance";
    private const string DeadOwnerInstance = "dead-instance";
    private const string SkipReason = "Docker is not available; skipping the SQL Server-backed voice admission scenario.";

    // ── Scenario 1: same participant, distinct conversations ─────────────────

    [SkippableFact]
    public async Task Concurrent_same_participant_distinct_conversations_exactly_one_admission_succeeds()
    {
        Skip.IfNot(DockerAvailable, SkipReason);

        await using var database = await CreateIsolatedDatabaseAsync();
        var connectionString = database.ConnectionString;

        var participant = NewParticipant();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var taskA = Task.Run(async () =>
        {
            using var db = CreateSqlContext(connectionString);
            var store = new SqlVoiceSessionStore(db);
            var session = Reserve(participant, new ChannelConversationId("conv-a"));
            await gate.Task;
            return await store.TryAdmitAsync(session, 5, CancellationToken.None);
        });

        var taskB = Task.Run(async () =>
        {
            using var db = CreateSqlContext(connectionString);
            var store = new SqlVoiceSessionStore(db);
            var session = Reserve(participant, new ChannelConversationId("conv-b"));
            await gate.Task;
            return await store.TryAdmitAsync(session, 5, CancellationToken.None);
        });

        gate.SetResult();
        var results = await Task.WhenAll(taskA, taskB);

        Assert.Single(results, r => r.Admitted);
        Assert.Single(results, r => !r.Admitted && r.DenialReason == VoiceAdmissionDenialReason.AlreadyActive);

        using var verify = CreateSqlContext(connectionString);
        var occupying = await verify.VoiceSessions.AsNoTracking()
            .CountAsync(e => e.ParticipantId == participant.Value && e.OccupiesSlot);
        Assert.Equal(1, occupying);
    }

    // ── Scenario 2: competing for final global cap slot ──────────────────────

    [SkippableFact]
    public async Task Concurrent_different_participants_competing_for_final_cap_slot_exactly_one_wins()
    {
        Skip.IfNot(DockerAvailable, SkipReason);

        await using var database = await CreateIsolatedDatabaseAsync();
        var connectionString = database.ConnectionString;

        const int cap = 3;

        // Seed cap-1 occupying sessions.
        for (var i = 0; i < cap - 1; i++)
        {
            using var seedDb = CreateSqlContext(connectionString);
            var seedStore = new SqlVoiceSessionStore(seedDb);
            var seedSession = Reserve(NewParticipant(), new ChannelConversationId($"seed-{i}"));
            var seedResult = await seedStore.TryAdmitAsync(seedSession, cap, CancellationToken.None);
            Assert.True(seedResult.Admitted, $"Seeding session {i} must succeed.");
        }

        // Two concurrent contenders for the one remaining slot.
        var contenders = Enumerable.Range(0, 2).Select(_ => NewParticipant()).ToArray();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tasks = contenders.Select((p, idx) => Task.Run(async () =>
        {
            using var db = CreateSqlContext(connectionString);
            var store = new SqlVoiceSessionStore(db);
            var session = Reserve(p, new ChannelConversationId($"contend-{idx}"));
            await gate.Task;
            return await store.TryAdmitAsync(session, cap, CancellationToken.None);
        })).ToArray();

        gate.SetResult();
        var results = await Task.WhenAll(tasks);

        Assert.Single(results, r => r.Admitted);
        Assert.Single(results, r => !r.Admitted && r.DenialReason == VoiceAdmissionDenialReason.GlobalCapReached);

        // Durable invariant: occupied count never exceeds cap.
        using var verify = CreateSqlContext(connectionString);
        var totalOccupying = await verify.VoiceSessions.AsNoTracking().CountAsync(e => e.OccupiesSlot);
        Assert.Equal(cap, totalOccupying);
    }

    // ── Scenario 3: crashed/stale Negotiating reservation cleanup ────────────

    [SkippableFact]
    public async Task Stale_negotiating_reservation_is_cleaned_up_and_same_participant_retries()
    {
        Skip.IfNot(DockerAvailable, SkipReason);

        await using var database = await CreateIsolatedDatabaseAsync();
        var connectionString = database.ConnectionString;

        var participant = NewParticipant();
        var time = new FakeTimeProvider(Epoch);
        var options = EnabledOptions(cap: 1);

        // Persist a Negotiating session from a dead instance with null ControlSessionId.
        VoiceSessionId staleId;
        {
            using var db = CreateSqlContext(connectionString);
            var store = new SqlVoiceSessionStore(db);
            var staleSession = VoiceSession.Reserve(
                participant,
                new ChannelConversationId("conv-stale"),
                DeadOwnerInstance,
                Epoch,
                options.ComputeDeadlines(Epoch));
            var admit = await store.TryAdmitAsync(staleSession, options.GlobalActiveCap, CancellationToken.None);
            Assert.True(admit.Admitted);
            staleId = admit.Session!.Id;

            // Verify ControlSessionId is null (Negotiating, never activated).
            var found = await store.FindByIdAsync(staleId, CancellationToken.None);
            Assert.NotNull(found);
            Assert.Null(found.ControlSessionId);
            Assert.Equal(VoiceSessionStatus.Negotiating, found.Status);
        }

        // Advance time beyond stale lease (heartbeat cutoff).
        var staleTime = Epoch + TimeSpan.FromMinutes(5);
        time.SetUtcNow(staleTime);

        // Run cleanup from current instance: find stale sessions, end them.
        {
            using var db = CreateSqlContext(connectionString);
            var store = new SqlVoiceSessionStore(db);
            var heartbeatCutoff = staleTime - TimeSpan.FromMinutes(2);
            var staleSessions = await store.FindStaleOwnerSessionsAsync(OwnerInstance, heartbeatCutoff, CancellationToken.None);

            Assert.Contains(staleSessions, s => s.Id == staleId);

            foreach (var stale in staleSessions.Where(s => s.Id == staleId))
            {
                stale.End(staleTime);
                var updated = await store.UpdateAsync(stale, VoiceSessionStatus.Negotiating, CancellationToken.None);
                Assert.True(updated, "Cleanup update of stale Negotiating session must succeed.");
            }
        }

        // Verify Ended + OccupiesSlot false.
        {
            using var db = CreateSqlContext(connectionString);
            var store = new SqlVoiceSessionStore(db);
            var ended = await store.FindByIdAsync(staleId, CancellationToken.None);
            Assert.NotNull(ended);
            Assert.Equal(VoiceSessionStatus.Ended, ended.Status);
            Assert.False(ended.OccupiesSlot);
        }

        // Same participant retries with cap=1 — must succeed now.
        {
            using var db = CreateSqlContext(connectionString);
            var store = new SqlVoiceSessionStore(db);
            var retrySession = Reserve(participant, new ChannelConversationId("conv-retry"), staleTime);
            var retry = await store.TryAdmitAsync(retrySession, options.GlobalActiveCap, CancellationToken.None);
            Assert.True(retry.Admitted, "Same participant must be admitted after stale cleanup.");
        }
    }

    // ── Scenario 4: full lifecycle reclaims all slots ────────────────────────

    [SkippableFact]
    public async Task Full_lifecycle_reclaims_all_slots()
    {
        Skip.IfNot(DockerAvailable, SkipReason);

        await using var database = await CreateIsolatedDatabaseAsync();
        var connectionString = database.ConnectionString;

        const int cap = 3;
        var gateway = new FakeVoiceLiveGateway();
        var time = new FakeTimeProvider(Epoch);
        var options = EnabledOptions(cap);

        // Admit cap participants; retain their session IDs and participant ownership.
        var participants = Enumerable.Range(0, cap).Select(_ => NewParticipant()).ToArray();
        var sessionIds = new VoiceSessionId[cap];

        for (var i = 0; i < cap; i++)
        {
            using var db = CreateSqlContext(connectionString);
            var store = new SqlVoiceSessionStore(db);
            var svc = new VoiceAdmissionService(store, gateway, options, time, OwnerInstance);
            var result = await svc.AdmitAsync(
                participants[i], new ChannelConversationId($"lifecycle-{i}"), "offer", CancellationToken.None);
            Assert.True(result.Admitted, $"Participant {i} admission must succeed.");
            sessionIds[i] = result.VoiceSessionId!.Value;
        }

        // Verify occupied count equals cap.
        using (var verify = CreateSqlContext(connectionString))
        {
            var occupied = await verify.VoiceSessions.AsNoTracking().CountAsync(e => e.OccupiesSlot);
            Assert.Equal(cap, occupied);
        }

        // Release each session through VoiceSessionReleaseService so the provider is terminated.
        for (var i = 0; i < cap; i++)
        {
            using var db = CreateSqlContext(connectionString);
            var store = new SqlVoiceSessionStore(db);
            var svc = new VoiceSessionReleaseService(store, gateway, time, options.IdleTimeout);
            await svc.ReleaseAsync(sessionIds[i], participants[i], CancellationToken.None);
        }

        // Verify each session is Ended, non-occupying, and the gateway no longer owns its handle.
        for (var i = 0; i < cap; i++)
        {
            using var db = CreateSqlContext(connectionString);
            var store = new SqlVoiceSessionStore(db);
            var ended = await store.FindByIdAsync(sessionIds[i], CancellationToken.None);
            Assert.NotNull(ended);
            Assert.Equal(VoiceSessionStatus.Ended, ended.Status);
            Assert.False(ended.OccupiesSlot);
            Assert.False(gateway.OwnsSession(ended.ControlSessionId!),
                "Gateway must not own a session after release.");
        }

        // Provider terminated all sessions: termination attempts exactly cap, no active sessions remain.
        Assert.Equal(cap, gateway.TerminationAttemptCount);
        Assert.Equal(0, gateway.ActiveSessionCount);

        // Verify zero occupied slots remain.
        using (var verify = CreateSqlContext(connectionString))
        {
            var remaining = await verify.VoiceSessions.AsNoTracking().CountAsync(e => e.OccupiesSlot);
            Assert.Equal(0, remaining);
        }

        // Admit N new distinct participants — must all succeed.
        var newParticipants = Enumerable.Range(0, cap).Select(_ => NewParticipant()).ToArray();
        for (var i = 0; i < cap; i++)
        {
            using var db = CreateSqlContext(connectionString);
            var store = new SqlVoiceSessionStore(db);
            var svc = new VoiceAdmissionService(store, gateway, options, time, OwnerInstance);
            var result = await svc.AdmitAsync(
                newParticipants[i], new ChannelConversationId($"readmit-{i}"), "offer", CancellationToken.None);
            Assert.True(result.Admitted, $"New participant {i} admission must succeed.");
        }

        // Durable occupied count equals N, no Negotiating leaks.
        using (var verify = CreateSqlContext(connectionString))
        {
            var finalOccupied = await verify.VoiceSessions.AsNoTracking().CountAsync(e => e.OccupiesSlot);
            Assert.Equal(cap, finalOccupied);

            var negotiating = await verify.VoiceSessions.AsNoTracking()
                .CountAsync(e => e.Status == nameof(VoiceSessionStatus.Negotiating));
            Assert.Equal(0, negotiating);
        }
    }

    // ── Scenario 5: concurrent activation vs release CAS ─────────────────────

    [SkippableFact]
    public async Task Concurrent_activation_and_release_CAS_guards_prevent_double_mutation()
    {
        Skip.IfNot(DockerAvailable, SkipReason);

        await using var database = await CreateIsolatedDatabaseAsync();
        var connectionString = database.ConnectionString;

        var participant = NewParticipant();

        // Admit a Negotiating session.
        VoiceSession session;
        {
            using var db = CreateSqlContext(connectionString);
            var store = new SqlVoiceSessionStore(db);
            var reserved = Reserve(participant, new ChannelConversationId("cas-conv"));
            var admit = await store.TryAdmitAsync(reserved, 5, CancellationToken.None);
            Assert.True(admit.Admitted);
            session = admit.Session!;
        }

        // Two concurrent updates: one activates (Negotiating→Active), one ends (expects Negotiating→Ended).
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var activating = Task.Run(async () =>
        {
            using var db = CreateSqlContext(connectionString);
            var store = new SqlVoiceSessionStore(db);
            var toActivate = await store.FindByIdAsync(session.Id, CancellationToken.None);
            Assert.NotNull(toActivate);
            toActivate.Activate("ctrl-1", Epoch + TimeSpan.FromSeconds(1));
            await gate.Task;
            return await store.UpdateAsync(toActivate, VoiceSessionStatus.Negotiating, CancellationToken.None);
        });

        var ending = Task.Run(async () =>
        {
            using var db = CreateSqlContext(connectionString);
            var store = new SqlVoiceSessionStore(db);
            var toEnd = await store.FindByIdAsync(session.Id, CancellationToken.None);
            Assert.NotNull(toEnd);
            toEnd.End(Epoch + TimeSpan.FromSeconds(1));
            await gate.Task;
            return await store.UpdateAsync(toEnd, VoiceSessionStatus.Negotiating, CancellationToken.None);
        });

        gate.SetResult();
        var results = await Task.WhenAll(activating, ending);

        // Exactly one CAS succeeds; the other fails (false).
        Assert.Single(results, r => r);
        Assert.Single(results, r => !r);

        // Verify durable state is consistent.
        using var verify = CreateSqlContext(connectionString);
        var final = await verify.VoiceSessions.AsNoTracking()
            .FirstAsync(e => e.Id == session.Id.Value);

        if (final.Status == nameof(VoiceSessionStatus.Active))
        {
            Assert.True(final.OccupiesSlot);
        }
        else
        {
            Assert.Equal(nameof(VoiceSessionStatus.Ended), final.Status);
            Assert.False(final.OccupiesSlot);
        }
    }

    // ── Scenario 8 invariant: no leaked rows after full cycle ────────────────

    [SkippableFact]
    public async Task No_slot_occupying_rows_exceed_cap_after_concurrent_admit_release_cycle()
    {
        Skip.IfNot(DockerAvailable, SkipReason);

        await using var database = await CreateIsolatedDatabaseAsync();
        var connectionString = database.ConnectionString;

        const int cap = 2;
        var gateway = new FakeVoiceLiveGateway();
        var time = new FakeTimeProvider(Epoch);
        var options = EnabledOptions(cap);

        // Concurrent admissions of cap+2 participants — at most cap succeed.
        var allParticipants = Enumerable.Range(0, cap + 2).Select(_ => NewParticipant()).ToArray();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var admitTasks = allParticipants.Select((p, idx) => Task.Run(async () =>
        {
            using var db = CreateSqlContext(connectionString);
            var store = new SqlVoiceSessionStore(db);
            var svc = new VoiceAdmissionService(store, gateway, options, time, OwnerInstance);
            await gate.Task;
            return await svc.AdmitAsync(p, new ChannelConversationId($"cap-check-{idx}"), "offer", CancellationToken.None);
        })).ToArray();

        gate.SetResult();
        var admitResults = await Task.WhenAll(admitTasks);

        var admitted = admitResults.Where(r => r.Admitted).ToArray();
        var denied = admitResults.Where(r => !r.Admitted).ToArray();

        Assert.Equal(cap, admitted.Length);
        Assert.All(denied, r => Assert.Equal(VoiceAdmissionDenialReason.GlobalCapReached, r.DenialReason));

        // Hard invariant: slot-occupying count never exceeds cap.
        using var verify = CreateSqlContext(connectionString);
        var occupying = await verify.VoiceSessions.AsNoTracking().CountAsync(e => e.OccupiesSlot);
        Assert.True(occupying <= cap, $"Occupied slots ({occupying}) must not exceed cap ({cap}).");
        Assert.Equal(cap, occupying);

        // No leaked Negotiating rows.
        var negotiating = await verify.VoiceSessions.AsNoTracking()
            .CountAsync(e => e.Status == nameof(VoiceSessionStatus.Negotiating));
        Assert.Equal(0, negotiating);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ParticipantId NewParticipant() => new(Guid.NewGuid());

    private static VoiceSessionDeadlines DefaultDeadlines(DateTimeOffset at) => new(
        ExpiresAt: at + TimeSpan.FromMinutes(30),
        WarningAt: at + TimeSpan.FromMinutes(25),
        IdleExpiresAt: at + TimeSpan.FromSeconds(60));

    private static VoiceSession Reserve(
        ParticipantId participant,
        ChannelConversationId conversation,
        DateTimeOffset? at = null)
    {
        var now = at ?? Epoch;
        return VoiceSession.Reserve(participant, conversation, OwnerInstance, now, DefaultDeadlines(now));
    }

    private static VoiceOptions EnabledOptions(int cap = 5) => new()
    {
        Enabled = true,
        Endpoint = "wss://test.services.ai.azure.com",
        Model = "gpt-4o-realtime",
        GlobalActiveCap = cap,
    };

    private async Task<SqlUserDatabase> CreateIsolatedDatabaseAsync()
    {
        var database = await SqlUserDatabase.CreateAsync(ServerConnectionString!, CancellationToken.None);

        using var db = CreateSqlContext(database.ConnectionString);
        await db.Database.MigrateAsync();

        return database;
    }

    private static MultiChannelAgentDbContext CreateSqlContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<MultiChannelAgentDbContext>()
            .UseSqlServer(connectionString)
            .Options;
        return new MultiChannelAgentDbContext(options);
    }
}
