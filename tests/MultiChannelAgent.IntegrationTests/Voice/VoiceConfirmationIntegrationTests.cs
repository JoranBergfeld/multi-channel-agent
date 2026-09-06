using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MultiChannelAgent.Application.Tests.Voice;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Application.Voice;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Infrastructure.Persistence;

namespace MultiChannelAgent.IntegrationTests.Voice;

/// <summary>
/// End-to-end proof that server-attested <see cref="InputModality"/> policy governs confirmation:
/// a voice-originated "confirm &lt;token&gt;" leaves the proposal Pending (no mutation applied), the
/// same "confirm &lt;token&gt;" as text consumes the proposal (mutation applied exactly once), and
/// voice-originated ordinary requests (reads) complete normally. This proves the policy through the
/// real HTTP application boundary, the real workflow pipeline, and the real persistence layer — not
/// by calling <see cref="DirectConfirmationEvidenceReader"/> directly.
/// </summary>
public sealed class VoiceConfirmationIntegrationTests : IAsyncLifetime
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

    // ── Scenario 1: voice confirm leaves pending, text confirm consumes ──────

    [Fact]
    public async Task Voice_confirm_leaves_proposal_pending_then_text_confirm_consumes_it()
    {
        var client = await SignInWithStockAsync("Confirm Steel Bolts", "Confirmation Warehouse", "Steel Bolts", 10m);

        // Produce a pending proposal: clearing stock to zero requires confirmation.
        var proposal = await TextOutcomeAsync(client, "native-proposal-1", "set stock Steel Bolts quantity 0");
        Assert.Equal("confirmation_required", proposal.GetProperty("category").GetString());
        var token = TokenOf(proposal);

        // Admit a voice session for the same Participant and ChannelConversation.
        var voiceSessionId = await AdmitVoiceSessionAsync(client);

        // Voice-originated "confirm <token>" — the server attests Voice, evidence is None, proposal
        // must stay Pending. The outcome is "invalid" because confirmation evidence is missing.
        var voiceConfirmTurnId = await SubmitVoiceTurnAcceptedAsync(
            client, "voice-confirm-1", $"confirm {token}", voiceSessionId);
        Assert.Equal(1, await ProcessPendingAsync());

        var voiceConfirmOutcome = await client.GetOutcomeAsync(voiceConfirmTurnId);
        Assert.NotNull(voiceConfirmOutcome);
        Assert.Equal("invalid", voiceConfirmOutcome!.Value.GetProperty("category").GetString());
        Assert.Equal("confirmation_evidence_missing", voiceConfirmOutcome.Value.GetProperty("code").GetString());

        // Persisted InboxEntry must carry InputModality.Voice.
        var (voiceModality, voiceCapabilities) = await ReadPersistedTurnAsync(voiceConfirmTurnId);
        Assert.Equal(InputModality.Voice, voiceModality);
        Assert.True(voiceCapabilities.HasFlag(ChannelCapabilities.Voice));

        // Proposal is still Pending.
        Assert.Equal(1, await CountPendingProposalsAsync());
        Assert.Equal(0, await CountChangeSetsAsync());

        // Text-originated "confirm <token>" — evidence is Confirmed, proposal consumed, mutation applied.
        var textConfirm = await TextOutcomeAsync(client, "text-confirm-1", $"confirm {token}");
        Assert.Equal("completed", textConfirm.GetProperty("category").GetString());

        // Persisted InboxEntry must carry InputModality.Text.
        var textTurnId = await FindTurnIdByNativeMessageAsync("text-confirm-1");
        var (textModality, _) = await ReadPersistedTurnAsync(textTurnId);
        Assert.Equal(InputModality.Text, textModality);

        // Proposal consumed: no longer pending, mutation applied exactly once.
        Assert.Equal(0, await CountPendingProposalsAsync());
        Assert.Equal(1, await CountChangeSetsAsync());
    }

    // ── Scenario 2: voice reject leaves pending, text reject rejects ─────────

    [Fact]
    public async Task Voice_reject_leaves_proposal_pending_then_text_reject_rejects_it()
    {
        var client = await SignInWithStockAsync("Reject Iron Nails", "Rejection Warehouse", "Iron Nails", 5m);

        var proposal = await TextOutcomeAsync(client, "native-proposal-2", "set stock Iron Nails quantity 0");
        Assert.Equal("confirmation_required", proposal.GetProperty("category").GetString());

        var voiceSessionId = await AdmitVoiceSessionAsync(client);

        // Voice-originated "reject" — evidence is None, proposal must stay Pending.
        var voiceRejectTurnId = await SubmitVoiceTurnAcceptedAsync(
            client, "voice-reject-1", "reject", voiceSessionId);
        Assert.Equal(1, await ProcessPendingAsync());

        var voiceRejectOutcome = await client.GetOutcomeAsync(voiceRejectTurnId);
        Assert.NotNull(voiceRejectOutcome);
        Assert.Equal("invalid", voiceRejectOutcome!.Value.GetProperty("category").GetString());
        Assert.Equal("rejection_evidence_missing", voiceRejectOutcome.Value.GetProperty("code").GetString());

        var (voiceModality, _) = await ReadPersistedTurnAsync(voiceRejectTurnId);
        Assert.Equal(InputModality.Voice, voiceModality);

        // Proposal still Pending.
        Assert.Equal(1, await CountPendingProposalsAsync());

        // Text-originated "reject" — evidence is Rejected, proposal settled.
        var textReject = await TextOutcomeAsync(client, "text-reject-1", "reject");
        Assert.Equal("completed", textReject.GetProperty("category").GetString());
        Assert.Equal("rejected", textReject.GetProperty("code").GetString());

        // Proposal is now Rejected, no mutation ever applied.
        Assert.Equal(0, await CountPendingProposalsAsync());
        Assert.Equal(nameof(ProposalStatus.Rejected), await SingleProposalStatusAsync());
        Assert.Equal(0, await CountChangeSetsAsync());
    }

    // ── Scenario 3: voice ordinary read request completes normally ───────────

    [Fact]
    public async Task Voice_ordinary_read_request_completes_normally()
    {
        var client = await SignInWithStockAsync("Read Copper Wire", "Read Warehouse", "Copper Wire", 3m);

        var voiceSessionId = await AdmitVoiceSessionAsync(client);

        var voiceReadTurnId = await SubmitVoiceTurnAcceptedAsync(
            client, "voice-read-1", "list stock", voiceSessionId);
        Assert.Equal(1, await ProcessPendingAsync());

        var voiceReadOutcome = await client.GetOutcomeAsync(voiceReadTurnId);
        Assert.NotNull(voiceReadOutcome);
        Assert.Equal("completed", voiceReadOutcome!.Value.GetProperty("category").GetString());

        var (modality, capabilities) = await ReadPersistedTurnAsync(voiceReadTurnId);
        Assert.Equal(InputModality.Voice, modality);
        Assert.True(capabilities.HasFlag(ChannelCapabilities.Voice));
    }

    // ── Scenario 4: invalid voiceSessionId falls back to Text and confirms ──

    /// <summary>
    /// Pins the intentional design: an invalid voiceSessionId fails open to <see cref="InputModality.Text"/>,
    /// so a "confirm" with an invalid session ID acts as a normal text confirmation. This is safe because
    /// the fallback is to the more-permissive modality, and the voice restriction is a privilege reduction
    /// for actually-attested voice input. Task 8 (<see cref="VoiceTurnProvenanceTests"/>) already proves
    /// the fallback for each invalid-session variant; this test pins the confirmation consequence.
    /// </summary>
    [Fact]
    public async Task Invalid_voiceSessionId_confirm_falls_back_to_text_and_confirms()
    {
        var client = await SignInWithStockAsync("Spoof Zinc Screws", "Spoof Warehouse", "Zinc Screws", 8m);

        var proposal = await TextOutcomeAsync(client, "native-proposal-spoof", "set stock Zinc Screws quantity 0");
        Assert.Equal("confirmation_required", proposal.GetProperty("category").GetString());
        var token = TokenOf(proposal);

        // Submit "confirm <token>" with a nonexistent voiceSessionId → falls back to Text → confirms.
        var spoofTurnId = await SubmitVoiceTurnAcceptedAsync(
            client, "spoof-confirm-1", $"confirm {token}", Guid.NewGuid().ToString());
        Assert.Equal(1, await ProcessPendingAsync());

        var spoofOutcome = await client.GetOutcomeAsync(spoofTurnId);
        Assert.NotNull(spoofOutcome);
        Assert.Equal("completed", spoofOutcome!.Value.GetProperty("category").GetString());

        // Persisted modality is Text (fallback), not Voice.
        var (modality, capabilities) = await ReadPersistedTurnAsync(spoofTurnId);
        Assert.Equal(InputModality.Text, modality);
        Assert.False(capabilities.HasFlag(ChannelCapabilities.Voice));

        // Proposal consumed.
        Assert.Equal(0, await CountPendingProposalsAsync());
        Assert.Equal(1, await CountChangeSetsAsync());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<ConversationTestClient> SignInWithStockAsync(
        string displayName, string inventoryName, string stockName, decimal quantity)
    {
        var httpClient = ConversationTestClient.CreateHttpsClient(_factory);
        var client = await ConversationTestClient.SignInAsync(httpClient, displayName);
        var inventoryId = await client.CreateAndSelectInventoryAsync(inventoryName);

        // Seed stock through the real workflow pipeline so every index and audit is consistent.
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
        var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/api/turns")
            {
                Content = JsonContent.Create(new { nativeMessageId, contentText, voiceSessionId }),
            },
            withCsrf: true);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("turnId").GetGuid();
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

    private static string TokenOf(JsonElement outcome)
    {
        var payload = outcome.GetProperty("payload");
        Assert.Equal("stock_proposal", payload.GetProperty("kind").GetString());
        var token = payload.GetProperty("token").GetString()!;
        Assert.True(ConfirmationToken.IsWellFormed(token));
        return token;
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

    private async Task<Guid> FindTurnIdByNativeMessageAsync(string nativeMessageId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        return await db.InboxEntries.AsNoTracking()
            .Where(e => e.NativeMessageId == nativeMessageId)
            .Select(e => e.TurnId)
            .SingleAsync();
    }

    private async Task<int> CountPendingProposalsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        return await db.ConfirmationProposals.AsNoTracking()
            .CountAsync(p => p.Status == nameof(ProposalStatus.Pending));
    }

    private async Task<string> SingleProposalStatusAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        return await db.ConfirmationProposals.AsNoTracking()
            .Select(p => p.Status)
            .SingleAsync();
    }

    private async Task<int> CountChangeSetsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        return await db.StockChangeSetOperations.AsNoTracking().CountAsync();
    }
}
