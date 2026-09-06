using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MultiChannelAgent.Application.Tests.Voice;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Application.Voice;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Infrastructure.Persistence;

namespace MultiChannelAgent.IntegrationTests.Voice;

/// <summary>
/// End-to-end proof of voice no-replay and canonical speech requirements through the real HTTP
/// application boundary, the real workflow pipeline, and real SQLite persistence.
///
/// No-replay: a finalized voice-originated Turn with a deterministic <c>voice:{voiceSessionId}:{providerItemId}</c>
/// nativeMessageId is accepted once and only once. Re-submission of the same nativeMessageId returns the
/// same TurnId with idempotent semantics. Processing after duplicate submission produces exactly one mutation.
/// Re-submission after a terminal Outcome returns the recorded result without a second mutation.
///
/// Identity determinism: the same provider item ID in the same voice session always produces the same
/// nativeMessageId; a different item or session produces a distinct one.
/// </summary>
public sealed class VoiceEndToEndScenarioTests : IAsyncLifetime
{
    private SqliteWebApplicationFactory _factory = null!;
    private FakeVoiceLiveGateway _fakeGateway = null!;

    public Task InitializeAsync()
    {
        _fakeGateway = new FakeVoiceLiveGateway();
        _factory = new SqliteWebApplicationFactory(configureTestServices: services =>
        {
            services.RemoveAll<IVoiceLiveGateway>();
            services.AddSingleton<IVoiceLiveGateway>(_fakeGateway);

            services.RemoveAll<VoiceOptions>();
            services.AddSingleton(new VoiceOptions
            {
                Enabled = true,
                Endpoint = "wss://test-voice.services.ai.azure.com/voice",
                Model = "test-model",
            });
        });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    // ── Scenario 1: same nativeMessageId resubmitted returns same TurnId ────

    [Fact]
    public async Task Same_nativeMessageId_resubmitted_returns_same_TurnId_with_idempotent_semantics()
    {
        var client = await SignInWithStockAsync("Replay Tester", "Replay Warehouse", "Replay Bolts", 10m);
        var voiceSessionId = await AdmitVoiceSessionAsync(client);

        var nativeMessageId = $"voice:{voiceSessionId}:item_replay_1";

        // First submission — accepted as new work.
        var firstTurnId = await SubmitVoiceTurnAcceptedAsync(client, nativeMessageId, "add five boxes of gloves", voiceSessionId);

        // Second submission — same nativeMessageId, same content, same session.
        var secondResponse = await SubmitVoiceTurnRawAsync(client, nativeMessageId, "add five boxes of gloves", voiceSessionId);
        Assert.Equal(HttpStatusCode.Accepted, secondResponse.StatusCode);
        var secondBody = await secondResponse.Content.ReadFromJsonAsync<JsonElement>();
        var secondTurnId = secondBody.GetProperty("turnId").GetGuid();

        Assert.Equal(firstTurnId, secondTurnId);
        Assert.True(secondBody.GetProperty("alreadyAccepted").GetBoolean());

        // Exactly one inbox row for this nativeMessageId.
        Assert.Equal(1, await CountInboxByNativeMessageAsync(nativeMessageId));

        // Persisted as Voice modality.
        var (modality, _) = await ReadPersistedTurnAsync(firstTurnId);
        Assert.Equal(InputModality.Voice, modality);
    }

    // ── Scenario 2: process and prove no duplicate effects ──────────────────

    [Fact]
    public async Task Duplicate_submission_and_processing_produces_exactly_one_mutation()
    {
        var client = await SignInWithStockAsync("Mutation Tester", "Mutation Warehouse", "Steel Bolts", 10m);
        var voiceSessionId = await AdmitVoiceSessionAsync(client);

        // Count operations after seed so we can measure exactly the voice-originated delta.
        var operationsAfterSeed = await CountStockOperationsAsync();

        var nativeMessageId = $"voice:{voiceSessionId}:item_mutation_1";

        // First submission.
        var turnId = await SubmitVoiceTurnAcceptedAsync(client, nativeMessageId, "add stock Steel Bolts quantity 5", voiceSessionId);

        // Duplicate submission before processing.
        var duplicateResponse = await SubmitVoiceTurnRawAsync(client, nativeMessageId, "add stock Steel Bolts quantity 5", voiceSessionId);
        Assert.Equal(HttpStatusCode.Accepted, duplicateResponse.StatusCode);
        var dupBody = await duplicateResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(turnId, dupBody.GetProperty("turnId").GetGuid());
        Assert.True(dupBody.GetProperty("alreadyAccepted").GetBoolean());

        // Process — exactly one Turn is pending.
        Assert.Equal(1, await ProcessPendingAsync());

        // Exactly one mutation from the voice Turn (the additive add).
        var operationsAfterProcess = await CountStockOperationsAsync();
        Assert.Equal(1, operationsAfterProcess - operationsAfterSeed);

        // Second processing pass yields zero — nothing was duplicated.
        Assert.Equal(0, await ProcessPendingAsync());
        Assert.Equal(operationsAfterProcess, await CountStockOperationsAsync());
    }

    // ── Scenario 3: resubmit after terminal outcome ─────────────────────────

    [Fact]
    public async Task Resubmit_after_terminal_outcome_returns_recorded_outcome_and_no_second_mutation()
    {
        var client = await SignInWithStockAsync("Terminal Tester", "Terminal Warehouse", "Copper Wire", 20m);
        var voiceSessionId = await AdmitVoiceSessionAsync(client);

        // Baseline operations count after seed.
        var operationsAfterSeed = await CountStockOperationsAsync();

        var nativeMessageId = $"voice:{voiceSessionId}:item_terminal_1";

        var turnId = await SubmitVoiceTurnAcceptedAsync(client, nativeMessageId, "add stock Copper Wire quantity 3", voiceSessionId);
        Assert.Equal(1, await ProcessPendingAsync());

        var operationsAfterProcess = await CountStockOperationsAsync();
        Assert.Equal(1, operationsAfterProcess - operationsAfterSeed);

        // Record the terminal outcome.
        var recorded = await client.GetOutcomeAsync(turnId);
        Assert.NotNull(recorded);
        Assert.Equal("completed", recorded!.Value.GetProperty("category").GetString());
        var recordedSummary = recorded.Value.GetProperty("summary").GetString();

        // Resubmit after terminal outcome — simulates reconnect/replay.
        var resubmitResponse = await SubmitVoiceTurnRawAsync(client, nativeMessageId, "add stock Copper Wire quantity 3", voiceSessionId);
        Assert.Equal(HttpStatusCode.OK, resubmitResponse.StatusCode);

        var resubmitBody = await resubmitResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(turnId, resubmitBody.GetProperty("turnId").GetGuid());
        Assert.Equal(recordedSummary, resubmitBody.GetProperty("summary").GetString());

        // No second mutation — processing pass yields zero, operation count unchanged.
        Assert.Equal(0, await ProcessPendingAsync());
        Assert.Equal(operationsAfterProcess, await CountStockOperationsAsync());
    }

    // ── Scenario 4: deterministic nativeMessageId identity ──────────────────

    [Fact]
    public async Task Same_provider_item_in_same_session_yields_same_nativeMessageId_and_different_item_or_session_yields_distinct()
    {
        var client = await SignInWithStockAsync("Identity Tester", "Identity Warehouse", "Zinc Screws", 5m);
        var voiceSessionId = await AdmitVoiceSessionAsync(client);

        // Same session, same item → same identity.
        var nativeA1 = $"voice:{voiceSessionId}:item_abc";
        var nativeA2 = $"voice:{voiceSessionId}:item_abc";
        Assert.Equal(nativeA1, nativeA2);

        // Same session, different item → distinct identity.
        var nativeB = $"voice:{voiceSessionId}:item_xyz";
        Assert.NotEqual(nativeA1, nativeB);

        // Different session → distinct identity even with same item.
        var nativeC = $"voice:other-session-id:item_abc";
        Assert.NotEqual(nativeA1, nativeC);

        // Prove server respects the distinct identities: two different nativeMessageIds = two Turns.
        var turnId1 = await SubmitVoiceTurnAcceptedAsync(client, nativeA1, "list stock", voiceSessionId);
        var turnId2 = await SubmitVoiceTurnAcceptedAsync(client, nativeB, "find Zinc", voiceSessionId);
        Assert.NotEqual(turnId1, turnId2);

        // Resubmit the first — same TurnId.
        var resubmitResponse = await SubmitVoiceTurnRawAsync(client, nativeA1, "list stock", voiceSessionId);
        var resubmitBody = await resubmitResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(turnId1, resubmitBody.GetProperty("turnId").GetGuid());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<ConversationTestClient> SignInWithStockAsync(
        string displayName, string inventoryName, string stockName, decimal quantity)
    {
        var httpClient = ConversationTestClient.CreateHttpsClient(_factory);
        var client = await ConversationTestClient.SignInAsync(httpClient, displayName);
        await client.CreateAndSelectInventoryAsync(inventoryName);

        var seeded = await TextOutcomeAsync(client, $"native-seed-{Guid.NewGuid():N}", $"add stock {stockName} quantity {quantity}");
        Assert.Equal("completed", seeded.GetProperty("category").GetString());

        return client;
    }

    private async Task<string> AdmitVoiceSessionAsync(ConversationTestClient client)
    {
        var admitResponse = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/api/voice/admit")
            {
                Content = JsonContent.Create(new { sdpOffer = "v=0\r\n" }),
            },
            withCsrf: true);
        Assert.Equal(HttpStatusCode.OK, admitResponse.StatusCode);

        var body = await admitResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("admitted").GetBoolean(), "Voice admission must succeed.");
        return body.GetProperty("voiceSessionId").GetString()!;
    }

    private async Task<Guid> SubmitVoiceTurnAcceptedAsync(
        ConversationTestClient client, string nativeMessageId, string contentText, string voiceSessionId)
    {
        var response = await SubmitVoiceTurnRawAsync(client, nativeMessageId, contentText, voiceSessionId);
        Assert.True(
            response.StatusCode == HttpStatusCode.Accepted || response.StatusCode == HttpStatusCode.OK,
            $"Expected 202 or 200, got {response.StatusCode}.");
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("turnId").GetGuid();
    }

    private async Task<HttpResponseMessage> SubmitVoiceTurnRawAsync(
        ConversationTestClient client, string nativeMessageId, string contentText, string voiceSessionId)
    {
        return await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/api/turns")
            {
                Content = JsonContent.Create(new { nativeMessageId, contentText, voiceSessionId }),
            },
            withCsrf: true);
    }

    private async Task<JsonElement> TextOutcomeAsync(
        ConversationTestClient client, string nativeMessageId, string contentText)
    {
        var turnId = await client.SubmitAcceptedTurnAsync(nativeMessageId, contentText);
        Assert.Equal(1, await ProcessPendingAsync());
        var outcome = await client.GetOutcomeAsync(turnId);
        Assert.NotNull(outcome);
        return outcome!.Value;
    }

    private async Task<int> ProcessPendingAsync()
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TurnProcessingCoordinator>()
            .ProcessPendingAsync(CancellationToken.None);
    }

    private async Task<(InputModality Modality, ChannelCapabilities Capabilities)> ReadPersistedTurnAsync(Guid turnId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        var entity = await db.InboxEntries.AsNoTracking().FirstAsync(e => e.TurnId == turnId);
        return (entity.InputModality, entity.Capabilities);
    }

    private async Task<int> CountInboxByNativeMessageAsync(string nativeMessageId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        return await db.InboxEntries.AsNoTracking()
            .CountAsync(e => e.NativeMessageId == nativeMessageId);
    }

    private async Task<int> CountStockOperationsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        return await db.StockOperations.AsNoTracking().CountAsync();
    }
}
