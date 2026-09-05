using System.Net;
using System.Runtime.ExceptionServices;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// The parts of this ticket that only a real database can prove: that the three new migrations apply
/// cleanly to a SQL Server schema built by every production migration before them, that the version
/// bump the persistence seam performs is safe under two concurrent writers, and that a conversation
/// reset racing a Turn acceptance always leaves both in a state that genuinely existed.
///
/// Backed by an ephemeral SQL Server container with production EF Core migrations applied, exactly
/// like every other SQL-backed scenario in this project.
/// </summary>
public sealed class WebConversationContinuitySqlScenarioTests : SqlIntegrationTestBase
{
    private const int DeadlockVictimErrorNumber = 1205;

    [SkippableFact]
    public async Task Every_migration_applies_and_the_new_tables_are_there_with_their_backfilled_rows()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available for the SQL Server-backed scenario.");

        var http = ConversationTestClient.CreateHttpsClient(Factory!);
        var participant = await ConversationTestClient.SignInAsync(http, "Migrating Participant");
        var inventoryId = await participant.CreateAndSelectInventoryAsync("Migrated Warehouse");

        using var scope = Factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(0L, (await db.InventoryVersions.AsNoTracking().SingleAsync(v => v.InventoryId == inventoryId)).Version);
        Assert.Empty(await db.TurnProgressEvents.AsNoTracking().ToListAsync());
    }

    [SkippableFact]
    public async Task A_turn_processed_against_real_sql_publishes_progress_a_version_and_a_resumable_stream()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available for the SQL Server-backed scenario.");

        var http = ConversationTestClient.CreateHttpsClient(Factory!);
        var participant = await ConversationTestClient.SignInAsync(http, "Streaming SQL Participant");
        var inventoryId = await participant.CreateAndSelectInventoryAsync("Streamed SQL Warehouse");

        var turnId = await participant.SubmitAcceptedTurnAsync("native-sql-1", "add stock Steel Bolts quantity 3");
        await ProcessUntilQuietAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var response = await participant.OpenTurnStreamAsync(turnId, cancellationToken: timeout.Token);
        await using var reader = await ServerSentEventReader.OpenAsync(response, timeout.Token);
        var events = await reader.ReadAsync(5, timeout.Token);

        Assert.Equal(["accepted", "processing", "part", "part", "outcome"], events.Select(e => e.Name));

        using var scope = Factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        Assert.True((await db.InventoryVersions.AsNoTracking().SingleAsync(v => v.InventoryId == inventoryId)).Version > 0L);
    }

    [SkippableFact]
    public async Task Two_genuinely_concurrent_commits_in_one_inventory_both_publish_and_neither_loses_its_bump()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available for the SQL Server-backed scenario.");

        var http = ConversationTestClient.CreateHttpsClient(Factory!);
        var participant = await ConversationTestClient.SignInAsync(http, "Concurrent SQL Participant");
        var inventoryId = await participant.CreateAndSelectInventoryAsync("Concurrent SQL Warehouse");

        // Read AFTER all setup, because setup publishes too: creating the Inventory seeds version 0,
        // and every audited change since - a Membership grant, for instance - has already bumped it.
        // Asserting against an assumed zero here would be asserting against the setup, not the seam.
        var baseline = await VersionAsync(inventoryId);

        // Two independent DI scopes, each with its own DbContext and its own transaction, started
        // before either is awaited. This is the concurrency the name claims: nothing serializes them
        // at the application boundary, so they meet at the database exactly as two replicas would.
        async Task CommitAuditedChangeAsync(string outcomeCode)
        {
            using var scope = Factory!.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();

            var occurredAt = DateTimeOffset.UtcNow;
            db.InventoryAudits.Add(new InventoryAuditEntity
            {
                Id = Guid.NewGuid(),
                EventType = nameof(AuditEventType.StockAdded),
                ActorKind = nameof(AuditActorKind.Participant),
                ActorId = participant.ParticipantIdentifier,
                InventoryId = inventoryId,
                SubjectParticipantId = null,
                OutcomeCode = outcomeCode,
                OccurredAtUtc = occurredAt,
                OccurredAtUtcTicks = occurredAt.UtcTicks,
                ExpiresAtUtc = occurredAt.AddDays(90),
            });

            await db.SaveChangesAsync();
        }

        var first = CommitAuditedChangeAsync("concurrent-a");
        var second = CommitAuditedChangeAsync("concurrent-b");
        await Task.WhenAll(first, second);

        // Two committed changes, two versions. A lost update - the failure a read-then-write counter
        // would have - would show up here as baseline + 1.
        Assert.Equal(baseline + 2, await VersionAsync(inventoryId));
    }

    [SkippableFact]
    public async Task Granting_membership_is_itself_an_audited_change_and_publishes_a_version()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available for the SQL Server-backed scenario.");

        var ownerHttp = ConversationTestClient.CreateHttpsClient(Factory!);
        var owner = await ConversationTestClient.SignInAsync(ownerHttp, "Granting SQL Owner");
        var inventoryId = await owner.CreateAndSelectInventoryAsync("Granting SQL Warehouse");

        var editorHttp = ConversationTestClient.CreateHttpsClient(Factory!);
        var editor = await ConversationTestClient.SignInAsync(editorHttp, "Granted SQL Editor");

        var before = await VersionAsync(inventoryId);
        await owner.GrantMembershipAsync(inventoryId, editor.ParticipantIdentifier, "Editor");

        // Recorded here so no other test has to rediscover it: governance is an audited change, so it
        // publishes exactly like a stock change does. Any test that counts versions must therefore
        // count from a baseline it captured, not from zero.
        Assert.Equal(before + 1, await VersionAsync(inventoryId));
    }

    [SkippableFact]
    public async Task Two_concurrent_resets_of_one_conversation_advance_two_generations_and_never_collide()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available for the SQL Server-backed scenario.");

        var http = ConversationTestClient.CreateHttpsClient(Factory!);
        var participant = await ConversationTestClient.SignInAsync(http, "Racing Reset Participant");
        await participant.CreateAndSelectInventoryAsync("Racing Reset Warehouse");
        var secondTab = participant.OpenAnotherTab();

        // Both requests are in flight before either is awaited, so this is a real race at the database.
        var statuses = await RunConcurrentlyRetryingDeadlockVictimAsync(
            participant.StartNewConversationAsync, secondTab.StartNewConversationAsync);

        Assert.All(statuses, status => Assert.Equal(HttpStatusCode.OK, status));

        using var scope = Factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        var binding = await db.FoundryConversationBindings.AsNoTracking().SingleAsync();

        Assert.Equal(3, binding.Generation);
    }

    [SkippableFact]
    public async Task A_reset_racing_an_acceptance_always_stamps_the_turn_with_a_generation_that_really_existed()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available for the SQL Server-backed scenario.");

        var http = ConversationTestClient.CreateHttpsClient(Factory!);
        var participant = await ConversationTestClient.SignInAsync(http, "Racing Acceptance Participant");
        await participant.CreateAndSelectInventoryAsync("Racing Acceptance Warehouse");
        var secondTab = participant.OpenAnotherTab();

        var submission = participant.SubmitTurnAsync("native-sql-reset-race", "list stock");
        var reset = secondTab.StartNewConversationAsync();

        var statuses = await SettleConcurrentRequestsAsync(submission, reset);
        Assert.Equal([HttpStatusCode.Accepted, HttpStatusCode.OK], statuses);

        await ProcessUntilQuietAsync();

        using var scope = Factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();

        var entry = await db.InboxEntries.AsNoTracking().SingleAsync();
        var binding = await db.FoundryConversationBindings.AsNoTracking().SingleAsync();

        // Whichever order they committed in, the Turn carries a generation that genuinely existed:
        // either the one before the reset, or the one the reset created. What it can never be is
        // unset, or a generation nobody ever established.
        Assert.NotNull(entry.FoundryConversationGeneration);
        Assert.InRange(entry.FoundryConversationGeneration!.Value, binding.Generation - 1, binding.Generation);
        Assert.NotNull(entry.FoundryConversationId);

        // And it still reached a terminal Outcome. A reset never abandons accepted work.
        Assert.NotNull(await db.Outcomes.AsNoTracking().FirstOrDefaultAsync(o => o.TurnId == entry.TurnId));
    }

    /// <summary>
    /// Starts every attempt before awaiting any of them, so they genuinely overlap, and re-runs any
    /// attempt SQL Server chose as a deadlock victim. A deadlock is the engine resolving contention,
    /// not the application misbehaving - the shipped reference administration concurrency tests treat
    /// it the same way - but the victim's work did NOT happen, so it is actually retried rather than
    /// pretended to have succeeded. Fabricating a success would leave the caller asserting a
    /// generation that was never reached.
    /// </summary>
    private static async Task<IReadOnlyList<HttpStatusCode>> RunConcurrentlyRetryingDeadlockVictimAsync(
        params Func<Task<HttpResponseMessage>>[] attempts)
    {
        var settled = await SettleAsync(attempts.Select(attempt => attempt()).ToList());
        var statuses = new List<HttpStatusCode>(attempts.Length);

        for (var index = 0; index < settled.Count; index++)
        {
            if (settled[index].Status is { } status)
            {
                statuses.Add(status);
                continue;
            }

            // Only SQL Server error 1205 - "chosen as the deadlock victim" - is retried; every other
            // fault is rethrown with its original stack, so this can never quietly absorb a real
            // failure or turn one into a second request nobody asked for.
            var fault = settled[index].Fault!;
            if (DeadlockVictim(fault) is null)
            {
                ExceptionDispatchInfo.Throw(fault);
            }

            using var retried = await attempts[index]();
            statuses.Add(retried.StatusCode);
        }

        return statuses;
    }

    /// <summary>
    /// Awaits requests that were already in flight and reports their status codes in the order they
    /// were started, rethrowing the first fault only once every one of them has settled.
    /// </summary>
    private static async Task<IReadOnlyList<HttpStatusCode>> SettleConcurrentRequestsAsync(
        params Task<HttpResponseMessage>[] started)
    {
        var settled = await SettleAsync(started);

        foreach (var request in settled)
        {
            if (request.Fault is { } fault)
            {
                ExceptionDispatchInfo.Throw(fault);
            }
        }

        return settled.Select(request => request.Status!.Value).ToList();
    }

    /// <summary>
    /// Awaits every started request to completion, disposing each response the moment its status has
    /// been read, and reports what each one did.
    ///
    /// Nothing is decided about any request until all of them have settled, because deciding earlier
    /// is what leaks: a fault in the first would otherwise abandon the response the second is still
    /// producing, and an <see cref="HttpResponseMessage"/> nobody disposes holds its buffered body
    /// open for the rest of the run. Only the status code escapes, so no live response can.
    /// </summary>
    private static async Task<IReadOnlyList<SettledRequest>> SettleAsync(
        IReadOnlyList<Task<HttpResponseMessage>> started)
    {
        var settled = new List<SettledRequest>(started.Count);

        foreach (var request in started)
        {
            try
            {
                using var response = await request;
                settled.Add(new SettledRequest(response.StatusCode, null));
            }
            catch (Exception exception)
            {
                settled.Add(new SettledRequest(null, exception));
            }
        }

        return settled;
    }

    /// <summary>What one started request did: the status it answered with, or the fault it raised.</summary>
    private readonly record struct SettledRequest(HttpStatusCode? Status, Exception? Fault);

    private static SqlException? DeadlockVictim(Exception exception) => exception switch
    {
        SqlException { Number: DeadlockVictimErrorNumber } deadlock => deadlock,
        { InnerException: { } inner } => DeadlockVictim(inner),
        _ => null,
    };

    private async Task<long> VersionAsync(Guid inventoryId)
    {
        using var scope = Factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();

        return (await db.InventoryVersions.AsNoTracking().SingleAsync(v => v.InventoryId == inventoryId)).Version;
    }

    private async Task ProcessUntilQuietAsync()
    {
        while (true)
        {
            using var scope = Factory!.Services.CreateScope();
            var coordinator = scope.ServiceProvider.GetRequiredService<TurnProcessingCoordinator>();
            if (await coordinator.ProcessPendingAsync(CancellationToken.None) == 0)
            {
                return;
            }
        }
    }
}
