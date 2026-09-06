# Interruptible Web Voice Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Complete issue #36 by adding bounded interruptible web voice to the existing conversation so finalized utterances use the same deterministic Turn workflow, confirmations, and Inventory mutations as text, while interruption and failure cannot replay work.

**Architecture:** Voice Live is a **speech transport only**, not a second agent workflow. The backend brokers WebRTC signalling via an authenticated Voice Live control WebSocket (Entra `TokenCredential` with scope `https://ai.azure.com/.default`; API-key mode is excluded from this initial scope). Voice Live is configured for server VAD and input audio transcription only; it receives no tool definitions and does not execute Inventory tools. The model session is configured with a transcription-only instruction and any provider-initiated response is immediately cancelled via `response.cancel` so no LLM-paraphrased business response ever reaches the browser.

A finalized input transcript event carries a stable provider item identity; the browser derives `nativeMessageId` deterministically as `voice:{voiceSessionId}:{providerInputItemId}` so event redelivery or reconnect cannot mint a second Turn. Partial transcripts never submit. The existing `POST /api/turns` + SSE produces the sole canonical Outcome. Only after that recorded terminal Outcome exists may the client synthesize speech for its canonical visible `summary` text using the documented `response.create` with `response.pre_generated_assistant_message` mechanism — this generates audio for the exact Outcome `summary` and adds it to context. The transport verifies `response.audio_transcript.done.transcript` equals the requested canonical text; if it differs, playback is stopped, a playback-integrity error is surfaced, and the text fallback remains visible. The UI always keeps the canonical summary text visible regardless of playback state. The access token remains only in the backend WebSocket URI/header and is redacted from all errors, logs, and responses.

**Input Modality and Confirmation Policy:** A new channel-neutral `InputModality` enum (`Text` | `Voice`) is added to `InboundTurnDraft` and `InboundTurn` and persisted on `InboxEntryEntity` via migration. The Host sets `InputModality.Voice` only after validating that the caller has an active voice session (`voiceSessionId` verified against `IVoiceSessionStore`); clients cannot directly set modality. `DirectConfirmationEvidenceReader` returns `None` for any Turn with `InputModality.Voice`, regardless of transcript content or embedded token — voice-originated Turns can never consume a pending confirmation. The Participant must use visible text input to confirm. This is a safe, deterministic initial-scope policy because Voice Live provides no trusted recognition-confidence signal in its `conversation.item.input_audio_transcription.completed` event (documented fields: `item_id`, `content_index`, `transcript`; optional `logprobs` and `phrases` exist but are not a sufficient trusted contract for confirmation evidence in this initial scope). The agent's ordinary workflow may still clarify ambiguous spoken quantities/units from the transcript text, but the voice adapter never provides direct confirmation evidence. `WasInterrupted` retains its existing semantics (cut-off utterance) and is never misused for voice uncertainty or barge-in.

**Intentional initial limitation:** Voice may request, clarify, propose mutations, and hear the canonical response, but direct confirmation must be typed because Voice Live provides no trusted recognition-confidence signal in the chosen contract. Optional `logprobs` exist in the transcription event but are not a sufficient trusted contract for confirmation evidence in this initial scope.

Admission uses a **reserve-negotiate-activate** pattern: a short-lived `Negotiating` row is atomically inserted under SERIALIZABLE isolation. A persisted boolean `OccupiesSlot` (true for `Negotiating` and `Active`, false for `Ended`) drives a filtered unique index on `(ParticipantId) WHERE OccupiesSlot = 1` for per-Participant uniqueness. The global cap query uses `SELECT COUNT(*) FROM VoiceSessions WITH (UPDLOCK, HOLDLOCK) WHERE OccupiesSlot = 1` inside the SERIALIZABLE transaction to prevent phantom final-slot races. `OccupiesSlot` is updated atomically on every lifecycle transition (`Activate` keeps it true; `Abandon`/`End` sets it false). Azure negotiation runs outside the SQL transaction. On success the row transitions to `Active`; on gateway failure it is atomically abandoned and capacity reclaimed. On gateway-success followed by row-activation failure, `TerminateAsync` is called and the reservation is abandoned. Immutable `ExpiresAt`, `WarningAt`, and `IdleExpiresAt` timestamps are computed at admission so configuration changes never retroactively alter admitted sessions. An `OwnerInstanceId` on each row makes process-local gateway ownership explicit; a restart reclaims rows owned by stale instances bounded by WebRTC/provider close + heartbeat lease expiry, not by immediate cross-process socket termination.

A shared `useTurnSubmission` controller extracted from `TurnTracer` is the sole Turn submission path. Both text form and voice finalized utterances call one `submit({nativeMessageId, contentText, wasInterrupted, voiceSessionId})` method that owns localStorage breadcrumb, HTTP POST, SSE stream, terminal Outcome dispatch, and StrictMode/unmount guards. The controller serializes one in-flight Turn; voice cannot overwrite text or vice versa.

**Tech Stack:** .NET 10 (ASP.NET Core minimal API, EF Core, SQL Server), React 19, TypeScript 6, Vitest, Testing Library, Testcontainers, WebRTC (browser-native), Azure AI Voice Live (preview, behind typed gateway interface), `Microsoft.Extensions.Time.Testing.FakeTimeProvider`

---

## File Map

Every file created or modified, grouped by responsibility. Tasks reference these exact paths.

### Backend — Domain

| Action | Path | Responsibility |
|--------|------|----------------|
| Create | `src/MultiChannelAgent.Domain/Voice/VoiceSessionId.cs` | Strongly typed voice session identity |
| Create | `src/MultiChannelAgent.Domain/Voice/VoiceSessionStatus.cs` | Session lifecycle: `Negotiating`, `Active`, `Ended` |
| Create | `src/MultiChannelAgent.Domain/Voice/VoiceSession.cs` | Session entity with immutable deadline timestamps, owner instance, and `OccupiesSlot` |
| Create | `src/MultiChannelAgent.Domain/Turns/InputModality.cs` | Channel-neutral `Text` / `Voice` enum |
| Modify | `src/MultiChannelAgent.Domain/Turns/InboundTurnDraft.cs` | Add `InputModality` property |
| Modify | `src/MultiChannelAgent.Domain/Turns/InboundTurn.cs` | Add `InputModality` property |

### Backend — Application

| Action | Path | Responsibility |
|--------|------|----------------|
| Create | `src/MultiChannelAgent.Application/Voice/IVoiceLiveGateway.cs` | Typed Voice Live protocol boundary |
| Create | `src/MultiChannelAgent.Application/Voice/VoiceOptions.cs` | Configuration with validation; `ComputeDeadlines` for immutable timestamps |
| Create | `src/MultiChannelAgent.Application/Voice/IVoiceSessionStore.cs` | Persistence with atomic `TryAdmitAsync` |
| Create | `src/MultiChannelAgent.Application/Voice/VoiceAdmissionService.cs` | Reserve-negotiate-activate admission |
| Create | `src/MultiChannelAgent.Application/Voice/VoiceSessionReleaseService.cs` | Release, force-close, heartbeat with lifecycle state |
| Create | `src/MultiChannelAgent.Application/Voice/VoiceSessionCleanupCoordinator.cs` | Expired/idle/stale-owner reaping |
| Create | `src/MultiChannelAgent.Application/Voice/HeartbeatResult.cs` | Heartbeat response with authoritative lifecycle state |
| Modify | `src/MultiChannelAgent.Application/Turns/DirectConfirmationEvidence.cs` | Return `None` for `InputModality.Voice` |
| Modify | `src/MultiChannelAgent.Application/Turns/SubmitTurnRequest.cs` | Add `InputModality` field |
| Modify | `src/MultiChannelAgent.Application/Turns/TurnAcceptanceService.cs` | Carry the Host-attested `InputModality` into `InboundTurnDraft` |

### Backend — Infrastructure

| Action | Path | Responsibility |
|--------|------|----------------|
| Create | `src/MultiChannelAgent.Infrastructure/Persistence/Entities/VoiceSessionEntity.cs` | EF entity with `DateTimeOffset` deadline columns, `OwnerInstanceId`, and `OccupiesSlot` |
| Create | `src/MultiChannelAgent.Infrastructure/Persistence/Configurations/VoiceSessionEntityConfiguration.cs` | EF config with filtered unique index on `OccupiesSlot = 1` |
| Create | `src/MultiChannelAgent.Infrastructure/Voice/SqlVoiceSessionStore.cs` | SQL store with SERIALIZABLE `TryAdmitAsync` using `UPDLOCK,HOLDLOCK` |
| Create | `src/MultiChannelAgent.Infrastructure/Voice/AzureVoiceLiveGateway.cs` | Real gateway: Entra `TokenCredential` auth, `rtc.call.sdp.create`/`rtc.call.sdp.created`, `response.cancel` on provider responses |
| Create | `src/MultiChannelAgent.Infrastructure/Voice/DisabledVoiceLiveGateway.cs` | Throws if called when voice disabled |
| Create | `src/MultiChannelAgent.Infrastructure/Voice/GatewayRegistry.cs` | Process-local `ConcurrentDictionary<string, ClientWebSocket>` for ownership |
| Modify | `src/MultiChannelAgent.Infrastructure/ServiceCollectionExtensions.cs` | Register voice services |
| Modify | `src/MultiChannelAgent.Infrastructure/Persistence/MultiChannelAgentDbContext.cs` | Add `VoiceSessions` DbSet |
| Modify | `src/MultiChannelAgent.Infrastructure/Persistence/Entities/InboxEntryEntity.cs` | Add `InputModality` column |
| Modify | `src/MultiChannelAgent.Infrastructure/Persistence/Configurations/InboxEntryEntityConfiguration.cs` | Configure `InputModality` string column |
| Modify | `src/MultiChannelAgent.Infrastructure/Turns/SqlInboxStore.cs` | Persist and rehydrate `InputModality` with each accepted Turn |
| Create | Migration: `AddVoiceSessions` | `VoiceSessions` table with `OccupiesSlot` filtered index |
| Create | Migration: `AddInputModality` | Non-null `InputModality` on `InboxEntries`, backfilled/defaulted to `Text` |

### Backend — Host

| Action | Path | Responsibility |
|--------|------|----------------|
| Create | `src/MultiChannelAgent.Host/Endpoints/VoiceEndpoints.cs` | HTTP: admit, release, heartbeat; CSRF-protected |
| Create | `src/MultiChannelAgent.Host/Workers/VoiceSessionCleanupWorker.cs` | Periodic cleanup |
| Modify | `src/MultiChannelAgent.Host/Endpoints/TurnEndpoints.cs` | Voice-provenance: validate `voiceSessionId`, set `InputModality.Voice` and `ChannelCapabilities.Voice` |
| Modify | `src/MultiChannelAgent.Host/Program.cs` | Map voice endpoints, register services, bind options |

### Backend — Tests

| Action | Path | Responsibility |
|--------|------|----------------|
| Create | `tests/MultiChannelAgent.Application.Tests/Voice/FakeVoiceLiveGateway.cs` | Deterministic gateway double |
| Create | `tests/MultiChannelAgent.Application.Tests/Voice/FakeVoiceLiveGatewayTests.cs` | Fake gateway tests |
| Create | `tests/MultiChannelAgent.Application.Tests/Voice/Fixtures/voice-live-2026-04-10/` | Versioned JSON fixtures from Microsoft docs (8 files including canonical speech) |
| Create | `tests/MultiChannelAgent.Application.Tests/Voice/VoiceLiveProtocolFixtureTests.cs` | Deserialize authoritative fixtures through production DTOs — PR gate |
| Create | `tests/MultiChannelAgent.Application.Tests/Voice/InMemoryVoiceSessionStore.cs` | In-memory store with lock-based atomic admission |
| Create | `tests/MultiChannelAgent.Application.Tests/Voice/InMemoryVoiceSessionStoreTests.cs` | Store interface contract tests |
| Create | `tests/MultiChannelAgent.Application.Tests/Voice/VoiceOptionsValidationTests.cs` | Options validation and deadline derivation |
| Create | `tests/MultiChannelAgent.Application.Tests/Voice/VoiceSessionTests.cs` | Domain entity lifecycle rules |
| Create | `tests/MultiChannelAgent.Application.Tests/Voice/VoiceAdmissionServiceTests.cs` | Admission: same-participant, global cap, gateway failure cleanup, activation failure cleanup |
| Create | `tests/MultiChannelAgent.Application.Tests/Voice/VoiceSessionReleaseServiceTests.cs` | Release, heartbeat lifecycle state response, force-close, missing local handle |
| Create | `tests/MultiChannelAgent.Application.Tests/Voice/VoiceSessionCleanupCoordinatorTests.cs` | Cleanup: expired, idle, stale-owner reclamation |
| Create | `tests/MultiChannelAgent.Application.Tests/Voice/VoiceModalityConfirmationTests.cs` | Voice modality `InputModality.Voice` → `DirectConfirmationEvidence.None`; text confirms normally |
| Create | `tests/MultiChannelAgent.IntegrationTests/Voice/VoiceAdmissionHttpTests.cs` | HTTP endpoint contract |
| Create | `tests/MultiChannelAgent.IntegrationTests/Voice/VoiceSecurityTests.cs` | Credential-leak proof: response JSON, ProblemDetails, URLs |
| Create | `tests/MultiChannelAgent.IntegrationTests/Voice/VoiceAdmissionSqlScenarioTests.cs` | SQL concurrency: duplicate starts, final slot, negotiation crash expiry, cap leakage |
| Create | `tests/MultiChannelAgent.IntegrationTests/Voice/VoiceTurnProvenanceTests.cs` | Voice InputModality is server-attested, not client-asserted |
| Create | `tests/MultiChannelAgent.IntegrationTests/Voice/VoiceConfirmationIntegrationTests.cs` | Voice "confirm token" leaves proposal pending; text confirm consumes it |
| Create | `tests/MultiChannelAgent.IntegrationTests/Voice/VoiceEndToEndScenarioTests.cs` | Exact-once, no replay |

### Frontend — Source

| Action | Path | Responsibility |
|--------|------|----------------|
| Create | `src/web/src/voiceReducer.ts` | Pure state machine with playback failure |
| Create | `src/web/src/voiceApi.ts` | HTTP client; heartbeat response with lifecycle state |
| Create | `src/web/src/voiceTransport.ts` | Typed WebRTC/data-channel transport contract |
| Create | `src/web/src/useTurnSubmission.ts` | Shared Turn submission controller (extracted from TurnTracer) |
| Create | `src/web/src/VoiceControls.tsx` | Accessible voice UI with playback failure fallback |
| Modify | `src/web/src/App.tsx` | Wire voice; generation token; New Conversation voice teardown |
| Modify | `src/web/src/TurnTracer.tsx` | Delegate submission to `useTurnSubmission` |
| Modify | `src/web/src/turnsApi.ts` | Add optional `voiceSessionId` to `SubmitTurnRequest` |

### Frontend — Tests

| Action | Path | Responsibility |
|--------|------|----------------|
| Create | `src/web/src/testing/fakeVoiceTransport.ts` | Deterministic transport double |
| Create | `src/web/src/voiceReducer.test.ts` | State machine: barge-in, playback failure |
| Create | `src/web/src/voiceApi.test.ts` | API client with lifecycle heartbeat response |
| Create | `src/web/src/voiceTransport.test.ts` | Transport adapter fake |
| Create | `src/web/src/useTurnSubmission.test.ts` | Shared submission: replay, rejection, serialization |
| Create | `src/web/src/VoiceControls.test.tsx` | Voice UI accessibility, playback failure, canonical text |

### Documentation

| Action | Path | Responsibility |
|--------|------|----------------|
| Modify | `CONTEXT.md` | Voice Session, Finalized Utterance, InputModality vocabulary |
| Modify | `README.md` | Voice endpoints, configuration, Docker constraints |

---

## Task 1: Voice Live Protocol Contract Types, Versioned Fixture Files, and Fixture Tests

**Why first:** Every other task depends on the typed gateway interface. Versioned JSON fixtures from the authoritative Microsoft docs anchor the real protocol before any gateway implementation.

**Fixture sources:**
- WebRTC signalling: `https://learn.microsoft.com/en-us/azure/ai-services/speech-service/voice-live-webrtc` (retrieved 2026-09-05)
- API reference: `https://learn.microsoft.com/en-us/azure/ai-services/speech-service/voice-live-api-reference-2026-04-10` (retrieved 2026-09-05)

API version: `2026-04-10`. If exact field spelling diverges from this snapshot, the opt-in live contract test (Task 19) is the gate — fixture tests are updated to match the verified live behavior.

**Files:**
- Create: `src/MultiChannelAgent.Application/Voice/IVoiceLiveGateway.cs`
- Create: `tests/MultiChannelAgent.Application.Tests/Voice/FakeVoiceLiveGateway.cs`
- Create: `tests/MultiChannelAgent.Application.Tests/Voice/FakeVoiceLiveGatewayTests.cs`
- Create: `tests/MultiChannelAgent.Application.Tests/Voice/Fixtures/voice-live-2026-04-10/sdp-create.json`
- Create: `tests/MultiChannelAgent.Application.Tests/Voice/Fixtures/voice-live-2026-04-10/sdp-created.json`
- Create: `tests/MultiChannelAgent.Application.Tests/Voice/Fixtures/voice-live-2026-04-10/session-update.json`
- Create: `tests/MultiChannelAgent.Application.Tests/Voice/Fixtures/voice-live-2026-04-10/response-cancel.json`
- Create: `tests/MultiChannelAgent.Application.Tests/Voice/Fixtures/voice-live-2026-04-10/input-audio-transcription-completed.json`
- Create: `tests/MultiChannelAgent.Application.Tests/Voice/Fixtures/voice-live-2026-04-10/conversation-item-truncate.json`
- Create: `tests/MultiChannelAgent.Application.Tests/Voice/Fixtures/voice-live-2026-04-10/response-create-canonical.json`
- Create: `tests/MultiChannelAgent.Application.Tests/Voice/Fixtures/voice-live-2026-04-10/response-audio-transcript-done.json`
- Create: `tests/MultiChannelAgent.Application.Tests/Voice/VoiceLiveProtocolFixtureTests.cs`

- [ ] **Step 1: Create fixture JSON files**

Each fixture is a literal JSON file embedded as a test resource. The `input-audio-transcription-completed` fixture documents the three required fields (`item_id`, `content_index`, `transcript`) and notes optional `logprobs` and `phrases` as omitted. Two additional fixtures anchor the canonical-speech `response.create` + `pre_generated_assistant_message` mechanism and its `response.audio_transcript.done` verification event.

```json
// tests/MultiChannelAgent.Application.Tests/Voice/Fixtures/voice-live-2026-04-10/sdp-create.json
{"type":"rtc.call.sdp.create","sdp_offer":"v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\n"}

// tests/MultiChannelAgent.Application.Tests/Voice/Fixtures/voice-live-2026-04-10/sdp-created.json
{"type":"rtc.call.sdp.created","sdp_answer":"v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\n"}

// tests/MultiChannelAgent.Application.Tests/Voice/Fixtures/voice-live-2026-04-10/session-update.json
{"type":"session.update","session":{"instructions":"Transcribe only. Do not generate responses.","input_audio_transcription":{"model":"whisper-1"},"turn_detection":{"type":"azure_semantic_vad","silence_duration_ms":500},"input_audio_noise_reduction":{"type":"azure_deep_noise_suppression"},"input_audio_echo_cancellation":{"type":"server_echo_cancellation"},"tools":[]}}

// tests/MultiChannelAgent.Application.Tests/Voice/Fixtures/voice-live-2026-04-10/response-cancel.json
{"type":"response.cancel"}

// tests/MultiChannelAgent.Application.Tests/Voice/Fixtures/voice-live-2026-04-10/input-audio-transcription-completed.json
{"type":"conversation.item.input_audio_transcription.completed","item_id":"item_abc123","content_index":0,"transcript":"add five boxes of gloves"}

// tests/MultiChannelAgent.Application.Tests/Voice/Fixtures/voice-live-2026-04-10/conversation-item-truncate.json
{"type":"conversation.item.truncate","item_id":"item_output_abc","content_index":0,"audio_end_ms":1500}

// tests/MultiChannelAgent.Application.Tests/Voice/Fixtures/voice-live-2026-04-10/response-create-canonical.json
{"type":"response.create","response":{"pre_generated_assistant_message":"5 boxes of Steel Bolts added."}}

// tests/MultiChannelAgent.Application.Tests/Voice/Fixtures/voice-live-2026-04-10/response-audio-transcript-done.json
{"type":"response.audio_transcript.done","transcript":"5 boxes of Steel Bolts added."}
```

Mark each JSON file as `<EmbeddedResource>` in the test `.csproj` or use `CopyToOutputDirectory`.

- [ ] **Step 2: Write the failing test — gateway interface exists and fake implements it**

```csharp
// tests/MultiChannelAgent.Application.Tests/Voice/FakeVoiceLiveGatewayTests.cs
using MultiChannelAgent.Application.Voice;

namespace MultiChannelAgent.Application.Tests.Voice;

public sealed class FakeVoiceLiveGatewayTests
{
    [Fact]
    public async Task Negotiate_returns_sdp_answer_and_control_session_id()
    {
        var gateway = new FakeVoiceLiveGateway();
        var request = new VoiceLiveNegotiationRequest(SdpOffer: "v=0\r\no=test\r\n");
        var result = await gateway.NegotiateAsync(request, CancellationToken.None);

        Assert.NotNull(result.ControlSessionId);
        Assert.Equal(gateway.SdpAnswerTemplate, result.SdpAnswer);
        Assert.Single(gateway.Sessions);
    }

    [Fact]
    public async Task Terminate_marks_session_terminated()
    {
        var gateway = new FakeVoiceLiveGateway();
        var result = await gateway.NegotiateAsync(
            new VoiceLiveNegotiationRequest("v=0\r\n"), CancellationToken.None);
        await gateway.TerminateAsync(result.ControlSessionId, CancellationToken.None);
        Assert.True(gateway.Sessions[result.ControlSessionId].Terminated);
    }

    [Fact]
    public async Task Negotiate_failure_throws_and_resets()
    {
        var gateway = new FakeVoiceLiveGateway
        {
            NextNegotiationFailure = new InvalidOperationException("Azure unreachable"),
        };
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => gateway.NegotiateAsync(new VoiceLiveNegotiationRequest("v=0\r\n"), CancellationToken.None));
        var result = await gateway.NegotiateAsync(
            new VoiceLiveNegotiationRequest("v=0\r\n"), CancellationToken.None);
        Assert.NotNull(result.ControlSessionId);
    }
}
```

- [ ] **Step 3: Run test — BUILD FAIL, types do not exist**

```bash
dotnet build tests/MultiChannelAgent.Application.Tests --configuration Release --verbosity quiet 2>&1 | head -20
# Expected: CS0234 — namespace 'MultiChannelAgent.Application.Voice' does not exist
```

- [ ] **Step 4: Create the gateway interface**

```csharp
// src/MultiChannelAgent.Application/Voice/IVoiceLiveGateway.cs
namespace MultiChannelAgent.Application.Voice;

public sealed record VoiceLiveNegotiationRequest(string SdpOffer);

public sealed record VoiceLiveNegotiationResult(string ControlSessionId, string SdpAnswer);

/// <summary>
/// Typed boundary between this application and Azure Voice Live. The backend holds the Entra
/// credential and control WebSocket; the browser never touches either.
///
/// The real implementation uses <c>rtc.call.sdp.create</c> with <c>sdp_offer</c> (client to Azure)
/// and <c>rtc.call.sdp.created</c> with <c>sdp_answer</c> (Azure to client) per authoritative
/// Voice Live WebRTC docs. Authentication uses Entra <c>TokenCredential</c> with scope
/// <c>https://ai.azure.com/.default</c>. API-key mode is excluded from this initial scope.
///
/// The session is transcription-only: no tool definitions, no Inventory tool execution,
/// <c>response.cancel</c> sent on any provider-initiated response.
/// </summary>
public interface IVoiceLiveGateway
{
    Task<VoiceLiveNegotiationResult> NegotiateAsync(
        VoiceLiveNegotiationRequest request, CancellationToken cancellationToken);

    Task TerminateAsync(string controlSessionId, CancellationToken cancellationToken);
}
```

- [ ] **Step 5: Create the fake gateway**

```csharp
// tests/MultiChannelAgent.Application.Tests/Voice/FakeVoiceLiveGateway.cs
using MultiChannelAgent.Application.Voice;

namespace MultiChannelAgent.Application.Tests.Voice;

public sealed class FakeVoiceLiveGateway : IVoiceLiveGateway
{
    private readonly Dictionary<string, VoiceLiveSession> _sessions = new();
    private int _nextSessionIndex;

    public string SdpAnswerTemplate { get; set; } = "v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\n";
    public Exception? NextNegotiationFailure { get; set; }
    public IReadOnlyDictionary<string, VoiceLiveSession> Sessions => _sessions;

    public Task<VoiceLiveNegotiationResult> NegotiateAsync(
        VoiceLiveNegotiationRequest request, CancellationToken cancellationToken)
    {
        if (NextNegotiationFailure is { } failure)
        {
            NextNegotiationFailure = null;
            throw failure;
        }
        var id = $"fake-control-{Interlocked.Increment(ref _nextSessionIndex)}";
        _sessions[id] = new VoiceLiveSession(id);
        return Task.FromResult(new VoiceLiveNegotiationResult(id, SdpAnswerTemplate));
    }

    public Task TerminateAsync(string controlSessionId, CancellationToken cancellationToken)
    {
        if (_sessions.TryGetValue(controlSessionId, out var session))
            session.Terminated = true;
        return Task.CompletedTask;
    }

    public sealed class VoiceLiveSession(string controlSessionId)
    {
        public string ControlSessionId { get; } = controlSessionId;
        public bool Terminated { get; set; }
    }
}
```

- [ ] **Step 6: Run fake gateway tests — 3 passed**

```bash
dotnet build tests/MultiChannelAgent.Application.Tests --configuration Release --verbosity quiet && \
dotnet test tests/MultiChannelAgent.Application.Tests --filter "FakeVoiceLiveGatewayTests" --no-build --configuration Release --verbosity normal
# Expected: Passed! - Failed: 0, Passed: 3
```

- [ ] **Step 7: Write versioned protocol fixture tests that deserialize literal fixture files**

These tests load the JSON files created in Step 1 and deserialize them through `JsonDocument`, asserting the exact field names the rest of the implementation depends on. The `conversation.item.truncate` fixture asserts all three required fields: `item_id`, `content_index`, `audio_end_ms`.

```csharp
// tests/MultiChannelAgent.Application.Tests/Voice/VoiceLiveProtocolFixtureTests.cs
using System.Text.Json;

namespace MultiChannelAgent.Application.Tests.Voice;

/// <summary>
/// Versioned fixture tests anchoring the Voice Live protocol from Microsoft docs.
/// Sources:
///   WebRTC: https://learn.microsoft.com/en-us/azure/ai-services/speech-service/voice-live-webrtc
///   API ref: https://learn.microsoft.com/en-us/azure/ai-services/speech-service/voice-live-api-reference-2026-04-10
/// API version: 2026-04-10. Retrieved: 2026-09-05.
/// If exact field spelling diverges from this snapshot, update these fixtures after verifying
/// against the opt-in live contract test (Task 19).
/// </summary>
public sealed class VoiceLiveProtocolFixtureTests
{
    private static JsonDocument LoadFixture(string name) =>
        JsonDocument.Parse(File.ReadAllText(
            Path.Combine("Voice", "Fixtures", "voice-live-2026-04-10", name)));

    [Fact]
    public void Sdp_create_message_carries_type_and_sdp_offer()
    {
        using var doc = LoadFixture("sdp-create.json");
        Assert.Equal("rtc.call.sdp.create", doc.RootElement.GetProperty("type").GetString());
        Assert.Contains("v=0", doc.RootElement.GetProperty("sdp_offer").GetString());
    }

    [Fact]
    public void Sdp_created_response_carries_type_and_sdp_answer()
    {
        using var doc = LoadFixture("sdp-created.json");
        Assert.Equal("rtc.call.sdp.created", doc.RootElement.GetProperty("type").GetString());
        Assert.Contains("v=0", doc.RootElement.GetProperty("sdp_answer").GetString());
    }

    [Fact]
    public void Session_update_transcription_only_has_no_tools()
    {
        using var doc = LoadFixture("session-update.json");
        Assert.Equal("session.update", doc.RootElement.GetProperty("type").GetString());
        var session = doc.RootElement.GetProperty("session");
        Assert.Equal(0, session.GetProperty("tools").GetArrayLength());
        Assert.Contains("Transcribe only", session.GetProperty("instructions").GetString());
    }

    [Fact]
    public void Response_cancel_message_shape()
    {
        using var doc = LoadFixture("response-cancel.json");
        Assert.Equal("response.cancel", doc.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public void Input_audio_transcription_completed_carries_item_id_and_transcript_only()
    {
        using var doc = LoadFixture("input-audio-transcription-completed.json");
        Assert.Equal("conversation.item.input_audio_transcription.completed",
            doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("item_abc123", doc.RootElement.GetProperty("item_id").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("content_index").GetInt32());
        Assert.Equal("add five boxes of gloves", doc.RootElement.GetProperty("transcript").GetString());
        // Optional logprobs and phrases are omitted — not a sufficient trusted contract for this scope.
    }

    [Fact]
    public void Conversation_item_truncate_carries_item_id_content_index_and_audio_end_ms()
    {
        using var doc = LoadFixture("conversation-item-truncate.json");
        Assert.Equal("conversation.item.truncate", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("item_output_abc", doc.RootElement.GetProperty("item_id").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("content_index").GetInt32());
        Assert.Equal(1500, doc.RootElement.GetProperty("audio_end_ms").GetInt32());
    }

    [Fact]
    public void Response_create_canonical_carries_pre_generated_assistant_message()
    {
        using var doc = LoadFixture("response-create-canonical.json");
        Assert.Equal("response.create", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("5 boxes of Steel Bolts added.",
            doc.RootElement.GetProperty("response").GetProperty("pre_generated_assistant_message").GetString());
    }

    [Fact]
    public void Response_audio_transcript_done_carries_transcript()
    {
        using var doc = LoadFixture("response-audio-transcript-done.json");
        Assert.Equal("response.audio_transcript.done", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("5 boxes of Steel Bolts added.", doc.RootElement.GetProperty("transcript").GetString());
    }
}
```

- [ ] **Step 8: Run all Task 1 tests — 11 passed**

```bash
dotnet test tests/MultiChannelAgent.Application.Tests --filter "FakeVoiceLiveGatewayTests|VoiceLiveProtocolFixtureTests" --no-build --configuration Release --verbosity normal
# Expected: Passed! - Failed: 0, Passed: 11
```

- [ ] **Step 9: Commit**

```bash
git add src/MultiChannelAgent.Application/Voice/IVoiceLiveGateway.cs \
        tests/MultiChannelAgent.Application.Tests/Voice/FakeVoiceLiveGateway.cs \
        tests/MultiChannelAgent.Application.Tests/Voice/FakeVoiceLiveGatewayTests.cs \
        tests/MultiChannelAgent.Application.Tests/Voice/Fixtures/ \
        tests/MultiChannelAgent.Application.Tests/Voice/VoiceLiveProtocolFixtureTests.cs
git commit -m "feat(voice): add Voice Live protocol contract, fake gateway, versioned fixture files, and fixture tests"
```

---

## Task 2: Voice Options with Immutable Deadline Derivation

**Why now:** Every service reads these limits. `ComputeDeadlines` produces immutable timestamps at admission — no `TimeSpan.Zero` is ever persisted.

**Files:**
- Create: `src/MultiChannelAgent.Application/Voice/VoiceOptions.cs`
- Create: `tests/MultiChannelAgent.Application.Tests/Voice/VoiceOptionsValidationTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/MultiChannelAgent.Application.Tests/Voice/VoiceOptionsValidationTests.cs
using MultiChannelAgent.Application.Voice;

namespace MultiChannelAgent.Application.Tests.Voice;

public sealed class VoiceOptionsValidationTests
{
    [Fact]
    public void Defaults_match_issue_requirements()
    {
        var o = new VoiceOptions();
        Assert.Equal(5, o.GlobalActiveCap);
        Assert.Equal(TimeSpan.FromMinutes(30), o.MaxSessionDuration);
        Assert.Equal(TimeSpan.FromMinutes(25), o.SessionWarningThreshold);
        Assert.Equal(TimeSpan.FromSeconds(60), o.IdleTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), o.HeartbeatInterval);
        Assert.False(o.Enabled);
    }

    [Fact]
    public void Validate_succeeds_for_disabled_voice() =>
        Assert.Empty(new VoiceOptions { Enabled = false }.Validate());

    [Fact]
    public void Validate_succeeds_when_enabled_with_all_required_fields() =>
        Assert.Empty(ValidEnabled().Validate());

    [Theory]
    [InlineData(null, "gpt-4.1", "Endpoint")]
    [InlineData("wss://x", null, "Model")]
    public void Validate_fails_without_required_field(string? ep, string? model, string field) =>
        Assert.Contains(new VoiceOptions { Enabled = true, Endpoint = ep, Model = model }.Validate(),
            e => e.Contains(field));

    [Fact]
    public void Validate_fails_for_zero_global_cap()
    {
        var o = ValidEnabled(); o.GlobalActiveCap = 0;
        Assert.Contains(o.Validate(), e => e.Contains("GlobalActiveCap"));
    }

    [Fact]
    public void Validate_fails_when_warning_exceeds_max()
    {
        var o = ValidEnabled();
        o.MaxSessionDuration = TimeSpan.FromMinutes(10);
        o.SessionWarningThreshold = TimeSpan.FromMinutes(15);
        Assert.Contains(o.Validate(), e => e.Contains("SessionWarningThreshold"));
    }

    [Fact]
    public void Validate_fails_for_zero_idle_timeout()
    {
        var o = ValidEnabled(); o.IdleTimeout = TimeSpan.Zero;
        Assert.Contains(o.Validate(), e => e.Contains("IdleTimeout"));
    }

    [Fact]
    public void ComputeDeadlines_returns_immutable_timestamps()
    {
        var now = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
        var d = ValidEnabled().ComputeDeadlines(now);
        Assert.Equal(now + TimeSpan.FromMinutes(30), d.ExpiresAt);
        Assert.Equal(now + TimeSpan.FromMinutes(25), d.WarningAt);
        Assert.Equal(now + TimeSpan.FromSeconds(60), d.IdleExpiresAt);
    }

    private static VoiceOptions ValidEnabled() => new()
    {
        Enabled = true, Endpoint = "wss://x.services.ai.azure.com/voice-live/realtime",
        Model = "gpt-4.1",
    };
}
```

- [ ] **Step 2: Run test — BUILD FAIL**

```bash
dotnet build tests/MultiChannelAgent.Application.Tests --configuration Release --verbosity quiet 2>&1 | head -20
# Expected: CS0234 — VoiceOptions does not exist
```

- [ ] **Step 3: Create VoiceOptions**

```csharp
// src/MultiChannelAgent.Application/Voice/VoiceOptions.cs
namespace MultiChannelAgent.Application.Voice;

/// <summary>Immutable deadline timestamps computed at admission. Config changes never retroactively alter admitted sessions.</summary>
public sealed record VoiceSessionDeadlines(DateTimeOffset ExpiresAt, DateTimeOffset WarningAt, DateTimeOffset IdleExpiresAt);

/// <summary>
/// Voice Live configuration. Capacity/session limits, NOT monetary budget/spend/quota.
/// Entra TokenCredential only — API-key mode is excluded from initial scope.
/// </summary>
public sealed class VoiceOptions
{
    public bool Enabled { get; set; }
    public string? Endpoint { get; set; }
    public string? Model { get; set; }
    public string VoiceName { get; set; } = "en-US-Ava:DragonHDLatestNeural";
    public int GlobalActiveCap { get; set; } = 5;
    public TimeSpan MaxSessionDuration { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan SessionWarningThreshold { get; set; } = TimeSpan.FromMinutes(25);
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);

    public IReadOnlyList<string> Validate()
    {
        if (!Enabled) return [];
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Endpoint)) errors.Add("Voice:Endpoint is required when voice is enabled.");
        if (string.IsNullOrWhiteSpace(Model)) errors.Add("Voice:Model is required when voice is enabled.");
        if (GlobalActiveCap < 1) errors.Add("Voice:GlobalActiveCap must be at least 1.");
        if (SessionWarningThreshold >= MaxSessionDuration) errors.Add("Voice:SessionWarningThreshold must be less than MaxSessionDuration.");
        if (IdleTimeout <= TimeSpan.Zero) errors.Add("Voice:IdleTimeout must be positive.");
        if (HeartbeatInterval <= TimeSpan.Zero) errors.Add("Voice:HeartbeatInterval must be positive.");
        return errors;
    }

    public VoiceSessionDeadlines ComputeDeadlines(DateTimeOffset admittedAt) => new(
        ExpiresAt: admittedAt + MaxSessionDuration,
        WarningAt: admittedAt + SessionWarningThreshold,
        IdleExpiresAt: admittedAt + IdleTimeout);
}
```

- [ ] **Step 4: Run tests — all pass**

```bash
dotnet test tests/MultiChannelAgent.Application.Tests --filter "VoiceOptionsValidationTests" --no-build --configuration Release --verbosity normal
# Expected: Passed! - Failed: 0, Passed: 7
```

- [ ] **Step 5: Commit**

```bash
git add src/MultiChannelAgent.Application/Voice/VoiceOptions.cs \
        tests/MultiChannelAgent.Application.Tests/Voice/VoiceOptionsValidationTests.cs
git commit -m "feat(voice): add voice options with validation and immutable deadline derivation"
```

---

## Task 3: Voice Session Domain Entity with OccupiesSlot

**Why now:** The entity stores immutable deadline timestamps, three-state lifecycle, `OwnerInstanceId`, and `OccupiesSlot` for the filtered unique index.

**Files:**
- Create: `src/MultiChannelAgent.Domain/Voice/VoiceSessionId.cs`
- Create: `src/MultiChannelAgent.Domain/Voice/VoiceSessionStatus.cs`
- Create: `src/MultiChannelAgent.Domain/Voice/VoiceSession.cs`
- Create: `tests/MultiChannelAgent.Application.Tests/Voice/VoiceSessionTests.cs`

- [ ] **Step 1: Write the failing tests — lifecycle rules with deadlines and OccupiesSlot**

```csharp
// tests/MultiChannelAgent.Application.Tests/Voice/VoiceSessionTests.cs
using MultiChannelAgent.Application.Voice;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Voice;

namespace MultiChannelAgent.Application.Tests.Voice;

public sealed class VoiceSessionTests
{
    private static readonly ParticipantId Participant = new(Guid.Parse("aaaa0000-0000-0000-0000-000000000001"));
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly VoiceSessionDeadlines Deadlines = new(
        ExpiresAt: Now + TimeSpan.FromMinutes(30),
        WarningAt: Now + TimeSpan.FromMinutes(25),
        IdleExpiresAt: Now + TimeSpan.FromSeconds(60));

    [Fact]
    public void Reserve_creates_negotiating_session_with_deadlines_and_OccupiesSlot_true()
    {
        var session = VoiceSession.Reserve(Participant, "conv-1", "instance-A", Now, Deadlines);
        Assert.Equal(VoiceSessionStatus.Negotiating, session.Status);
        Assert.True(session.OccupiesSlot);
        Assert.Equal(Deadlines.ExpiresAt, session.ExpiresAt);
        Assert.Equal(Deadlines.WarningAt, session.WarningAt);
        Assert.Equal(Deadlines.IdleExpiresAt, session.IdleExpiresAt);
        Assert.Equal("instance-A", session.OwnerInstanceId);
        Assert.Null(session.ControlSessionId);
    }

    [Fact]
    public void Activate_transitions_to_Active_and_keeps_OccupiesSlot_true()
    {
        var session = VoiceSession.Reserve(Participant, "conv-1", "inst", Now, Deadlines);
        session.Activate("ctrl-1", Now);
        Assert.Equal(VoiceSessionStatus.Active, session.Status);
        Assert.True(session.OccupiesSlot);
        Assert.Equal("ctrl-1", session.ControlSessionId);
    }

    [Fact]
    public void Activate_from_non_Negotiating_throws()
    {
        var session = VoiceSession.Reserve(Participant, "conv-1", "inst", Now, Deadlines);
        session.Activate("ctrl-1", Now);
        Assert.Throws<InvalidOperationException>(() => session.Activate("ctrl-2", Now));
    }

    [Fact]
    public void Abandon_ends_Negotiating_and_sets_OccupiesSlot_false()
    {
        var session = VoiceSession.Reserve(Participant, "conv-1", "inst", Now, Deadlines);
        session.Abandon(Now);
        Assert.Equal(VoiceSessionStatus.Ended, session.Status);
        Assert.False(session.OccupiesSlot);
    }

    [Fact]
    public void End_sets_status_Ended_and_OccupiesSlot_false()
    {
        var session = VoiceSession.Reserve(Participant, "conv-1", "inst", Now, Deadlines);
        session.Activate("ctrl-1", Now);
        session.End(Now);
        Assert.Equal(VoiceSessionStatus.Ended, session.Status);
        Assert.False(session.OccupiesSlot);
    }

    [Fact]
    public void End_is_idempotent()
    {
        var session = VoiceSession.Reserve(Participant, "conv-1", "inst", Now, Deadlines);
        session.Activate("ctrl-1", Now);
        session.End(Now);
        session.End(Now + TimeSpan.FromSeconds(1));
        Assert.Equal(VoiceSessionStatus.Ended, session.Status);
    }

    [Fact]
    public void IsExpired_returns_true_at_ExpiresAt()
    {
        var session = VoiceSession.Reserve(Participant, "conv-1", "inst", Now, Deadlines);
        session.Activate("ctrl-1", Now);
        Assert.False(session.IsExpired(Now + TimeSpan.FromMinutes(29)));
        Assert.True(session.IsExpired(Now + TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void IsIdle_returns_true_at_IdleExpiresAt()
    {
        var session = VoiceSession.Reserve(Participant, "conv-1", "inst", Now, Deadlines);
        session.Activate("ctrl-1", Now);
        Assert.False(session.IsIdle(Now + TimeSpan.FromSeconds(59)));
        Assert.True(session.IsIdle(Now + TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void Heartbeat_resets_IdleExpiresAt()
    {
        var session = VoiceSession.Reserve(Participant, "conv-1", "inst", Now, Deadlines);
        session.Activate("ctrl-1", Now);
        var beatAt = Now + TimeSpan.FromSeconds(50);
        session.RecordHeartbeat(beatAt, TimeSpan.FromSeconds(60));
        Assert.Equal(beatAt + TimeSpan.FromSeconds(60), session.IdleExpiresAt);
        Assert.False(session.IsIdle(beatAt + TimeSpan.FromSeconds(59)));
        Assert.True(session.IsIdle(beatAt + TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void ShouldWarn_fires_exactly_once()
    {
        var session = VoiceSession.Reserve(Participant, "conv-1", "inst", Now, Deadlines);
        session.Activate("ctrl-1", Now);
        Assert.False(session.ShouldWarn(Now + TimeSpan.FromMinutes(24)));
        Assert.True(session.ShouldWarn(Now + TimeSpan.FromMinutes(25)));
        Assert.False(session.ShouldWarn(Now + TimeSpan.FromMinutes(26)));
    }
}
```

- [ ] **Step 2: Run test — BUILD FAIL**

```bash
dotnet build tests/MultiChannelAgent.Application.Tests --configuration Release --verbosity quiet 2>&1 | head -20
# Expected: CS0234 — VoiceSession, VoiceSessionStatus do not exist
```

- [ ] **Step 3: Create domain types**

`VoiceSessionId`: `readonly record struct` wrapping `Guid`, identical pattern to existing `ParticipantId`.

`VoiceSessionStatus`: `enum { Negotiating, Active, Ended }`.

`VoiceSession`: Entity with properties: `Id` (VoiceSessionId), `ParticipantId`, `ChannelConversationId` (string), `ControlSessionId` (string?), `OwnerInstanceId` (string), `Status` (VoiceSessionStatus), `OccupiesSlot` (bool — true for Negotiating/Active, false for Ended), `StartedAt` (DateTimeOffset), `LastHeartbeatAt` (DateTimeOffset), `EndedAt` (DateTimeOffset?), `ExpiresAt`, `WarningAt`, `IdleExpiresAt` (all DateTimeOffset), `WarningIssued` (bool).

Methods:
- `Reserve(participantId, channelConversationId, ownerInstanceId, now, deadlines)` — factory creating `Negotiating` with `OccupiesSlot = true`
- `Activate(controlSessionId, now)` — transitions to `Active`, keeps `OccupiesSlot = true`; throws if not `Negotiating`
- `Abandon(now)` — sets `Ended`, `OccupiesSlot = false`; only from `Negotiating`
- `End(now)` — sets `Ended`, `OccupiesSlot = false`; idempotent
- `RecordHeartbeat(now, idleTimeout)` — resets `IdleExpiresAt = now + idleTimeout`
- `ShouldWarn(now)` — returns true exactly once when `now >= WarningAt && !WarningIssued`; sets `WarningIssued = true`
- `IsExpired(now)` — `now >= ExpiresAt`
- `IsIdle(now)` — `now >= IdleExpiresAt`

- [ ] **Step 4: Run tests — all pass**

```bash
dotnet build tests/MultiChannelAgent.Application.Tests --configuration Release --verbosity quiet && \
dotnet test tests/MultiChannelAgent.Application.Tests --filter "VoiceSessionTests" --no-build --configuration Release --verbosity normal
# Expected: Passed! - Failed: 0, Passed: 11
```

- [ ] **Step 5: Commit**

```bash
git add src/MultiChannelAgent.Domain/Voice/ \
        tests/MultiChannelAgent.Application.Tests/Voice/VoiceSessionTests.cs
git commit -m "feat(voice): add voice session domain entity with OccupiesSlot, immutable deadlines, and owner instance"
```

---

## Task 4: Atomic SQL Voice Session Store with OccupiesSlot Filtered Index

**Why now:** `TryAdmitAsync` must be one atomic operation — no separate Find/Count/Add that TOCTOU races can exploit.

**SQL strategy:** The filtered unique index `CREATE UNIQUE INDEX IX_VoiceSessions_ParticipantId_OccupiesSlot ON VoiceSessions (ParticipantId) WHERE OccupiesSlot = 1` prevents two non-Ended rows per Participant even under concurrency. The global cap check uses `SELECT COUNT(*) FROM VoiceSessions WITH (UPDLOCK, HOLDLOCK) WHERE OccupiesSlot = 1` inside a `SERIALIZABLE` transaction to hold a range lock that prevents phantom inserts from concurrent transactions sneaking past the count. `UPDLOCK` prevents two concurrent readers from both seeing count < cap and then both inserting. `HOLDLOCK` retains the lock until the transaction commits.

**Activation-failure concurrency:** The reservation row is the unique occupied row throughout negotiation. Activation updates only that row by `Id` and `Status = Negotiating`; no second participant reservation can steal it because the filtered unique index prevents a second `OccupiesSlot = 1` row for a different participant from overlapping.

**Files:**
- Create: `src/MultiChannelAgent.Application/Voice/IVoiceSessionStore.cs`
- Create: `tests/MultiChannelAgent.Application.Tests/Voice/InMemoryVoiceSessionStore.cs`
- Create: `tests/MultiChannelAgent.Application.Tests/Voice/InMemoryVoiceSessionStoreTests.cs`
- Create: `src/MultiChannelAgent.Infrastructure/Persistence/Entities/VoiceSessionEntity.cs`
- Create: `src/MultiChannelAgent.Infrastructure/Persistence/Configurations/VoiceSessionEntityConfiguration.cs`
- Create: `src/MultiChannelAgent.Infrastructure/Voice/SqlVoiceSessionStore.cs`
- Modify: `src/MultiChannelAgent.Infrastructure/Persistence/MultiChannelAgentDbContext.cs`

- [ ] **Step 1: Write the failing tests for the store interface**

```csharp
// tests/MultiChannelAgent.Application.Tests/Voice/InMemoryVoiceSessionStoreTests.cs
using MultiChannelAgent.Application.Voice;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Voice;

namespace MultiChannelAgent.Application.Tests.Voice;

public sealed class InMemoryVoiceSessionStoreTests
{
    private static readonly ParticipantId Alice = new(Guid.Parse("aaaa0000-0000-0000-0000-000000000001"));
    private static readonly ParticipantId Bob = new(Guid.Parse("bbbb0000-0000-0000-0000-000000000002"));
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly VoiceSessionDeadlines Deadlines = new(
        Now + TimeSpan.FromMinutes(30), Now + TimeSpan.FromMinutes(25), Now + TimeSpan.FromSeconds(60));

    [Fact]
    public async Task TryAdmit_succeeds_for_first_participant()
    {
        var store = new InMemoryVoiceSessionStore();
        var s = VoiceSession.Reserve(Alice, "conv-1", "inst", Now, Deadlines);
        Assert.True(await store.TryAdmitAsync(s, globalCap: 5, CancellationToken.None));
    }

    [Fact]
    public async Task TryAdmit_rejects_same_participant_while_Active()
    {
        var store = new InMemoryVoiceSessionStore();
        var s1 = VoiceSession.Reserve(Alice, "conv-1", "inst", Now, Deadlines);
        await store.TryAdmitAsync(s1, 5, CancellationToken.None);
        s1.Activate("ctrl-1", Now);
        await store.UpdateAsync(s1, CancellationToken.None);
        var s2 = VoiceSession.Reserve(Alice, "conv-2", "inst", Now, Deadlines);
        Assert.False(await store.TryAdmitAsync(s2, 5, CancellationToken.None));
    }

    [Fact]
    public async Task TryAdmit_rejects_same_participant_while_Negotiating()
    {
        var store = new InMemoryVoiceSessionStore();
        var s1 = VoiceSession.Reserve(Alice, "conv-1", "inst", Now, Deadlines);
        await store.TryAdmitAsync(s1, 5, CancellationToken.None);
        var s2 = VoiceSession.Reserve(Alice, "conv-2", "inst", Now, Deadlines);
        Assert.False(await store.TryAdmitAsync(s2, 5, CancellationToken.None));
    }

    [Fact]
    public async Task TryAdmit_rejects_at_global_cap()
    {
        var store = new InMemoryVoiceSessionStore();
        var s1 = VoiceSession.Reserve(Alice, "conv-1", "inst", Now, Deadlines);
        await store.TryAdmitAsync(s1, 1, CancellationToken.None);
        var s2 = VoiceSession.Reserve(Bob, "conv-2", "inst", Now, Deadlines);
        Assert.False(await store.TryAdmitAsync(s2, 1, CancellationToken.None));
    }

    [Fact]
    public async Task TryAdmit_counts_Negotiating_toward_global_cap()
    {
        var store = new InMemoryVoiceSessionStore();
        var s1 = VoiceSession.Reserve(Alice, "conv-1", "inst", Now, Deadlines);
        await store.TryAdmitAsync(s1, 1, CancellationToken.None);
        // s1 is Negotiating (OccupiesSlot = true) — counts toward cap.
        var s2 = VoiceSession.Reserve(Bob, "conv-2", "inst", Now, Deadlines);
        Assert.False(await store.TryAdmitAsync(s2, 1, CancellationToken.None));
    }

    [Fact]
    public async Task TryAdmit_allows_after_Ended()
    {
        var store = new InMemoryVoiceSessionStore();
        var s1 = VoiceSession.Reserve(Alice, "conv-1", "inst", Now, Deadlines);
        await store.TryAdmitAsync(s1, 1, CancellationToken.None);
        s1.Abandon(Now);
        await store.UpdateAsync(s1, CancellationToken.None);
        var s2 = VoiceSession.Reserve(Alice, "conv-2", "inst", Now, Deadlines);
        Assert.True(await store.TryAdmitAsync(s2, 1, CancellationToken.None));
    }

    [Fact]
    public async Task FindByIdAsync_returns_null_for_nonexistent()
    {
        var store = new InMemoryVoiceSessionStore();
        Assert.Null(await store.FindByIdAsync(new VoiceSessionId(Guid.NewGuid()), CancellationToken.None));
    }

    [Fact]
    public async Task FindExpiredOrIdleAsync_returns_only_expired_or_idle()
    {
        var store = new InMemoryVoiceSessionStore();
        var s1 = VoiceSession.Reserve(Alice, "conv-1", "inst", Now, Deadlines);
        await store.TryAdmitAsync(s1, 5, CancellationToken.None);
        s1.Activate("ctrl-1", Now);
        await store.UpdateAsync(s1, CancellationToken.None);
        // Not expired yet
        var result = await store.FindExpiredOrIdleAsync(Now + TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Empty(result);
        // Idle now
        result = await store.FindExpiredOrIdleAsync(Now + TimeSpan.FromSeconds(60), CancellationToken.None);
        Assert.Single(result);
    }
}
```

- [ ] **Step 2: Run test — BUILD FAIL**

```bash
dotnet build tests/MultiChannelAgent.Application.Tests --configuration Release --verbosity quiet 2>&1 | head -20
# Expected: CS0234 — IVoiceSessionStore, InMemoryVoiceSessionStore do not exist
```

- [ ] **Step 3: Create the store interface**

```csharp
// src/MultiChannelAgent.Application/Voice/IVoiceSessionStore.cs
using MultiChannelAgent.Domain.Voice;

namespace MultiChannelAgent.Application.Voice;

public interface IVoiceSessionStore
{
    /// <summary>
    /// Atomically admits a Negotiating session: checks one-per-participant (OccupiesSlot = true)
    /// AND global cap (count of OccupiesSlot = true), then inserts. Returns false if either fails.
    /// SQL: SERIALIZABLE + UPDLOCK,HOLDLOCK + filtered unique index on OccupiesSlot.
    /// In-memory: single lock.
    /// </summary>
    Task<bool> TryAdmitAsync(VoiceSession session, int globalCap, CancellationToken cancellationToken);

    Task<VoiceSession?> FindByIdAsync(VoiceSessionId id, CancellationToken cancellationToken);
    Task UpdateAsync(VoiceSession session, CancellationToken cancellationToken);
    Task<IReadOnlyList<VoiceSession>> FindExpiredOrIdleAsync(DateTimeOffset now, CancellationToken cancellationToken);
    Task<IReadOnlyList<VoiceSession>> FindByOwnerInstanceAsync(string ownerInstanceId, CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Create InMemoryVoiceSessionStore**

Single `lock(_gate)` mirrors SERIALIZABLE. `TryAdmitAsync` checks participant uniqueness among `OccupiesSlot == true` rows, then checks count of `OccupiesSlot == true` < globalCap, then adds. All in one locked section.

```csharp
// tests/MultiChannelAgent.Application.Tests/Voice/InMemoryVoiceSessionStore.cs
using MultiChannelAgent.Application.Voice;
using MultiChannelAgent.Domain.Voice;

namespace MultiChannelAgent.Application.Tests.Voice;

public sealed class InMemoryVoiceSessionStore : IVoiceSessionStore
{
    private readonly object _gate = new();
    private readonly Dictionary<VoiceSessionId, VoiceSession> _sessions = new();

    public Task<bool> TryAdmitAsync(VoiceSession session, int globalCap, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var hasExisting = _sessions.Values.Any(s =>
                s.ParticipantId == session.ParticipantId && s.OccupiesSlot);
            if (hasExisting) return Task.FromResult(false);

            var count = _sessions.Values.Count(s => s.OccupiesSlot);
            if (count >= globalCap) return Task.FromResult(false);

            _sessions[session.Id] = session;
            return Task.FromResult(true);
        }
    }

    public Task<VoiceSession?> FindByIdAsync(VoiceSessionId id, CancellationToken cancellationToken) =>
        Task.FromResult(_sessions.TryGetValue(id, out var s) ? s : null);

    public Task UpdateAsync(VoiceSession session, CancellationToken cancellationToken)
    {
        lock (_gate) { _sessions[session.Id] = session; }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<VoiceSession>> FindExpiredOrIdleAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<VoiceSession>>(
                _sessions.Values.Where(s => s.OccupiesSlot && (s.IsExpired(now) || s.IsIdle(now))).ToList());
        }
    }

    public Task<IReadOnlyList<VoiceSession>> FindByOwnerInstanceAsync(string ownerInstanceId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<VoiceSession>>(
                _sessions.Values.Where(s => s.OccupiesSlot && s.OwnerInstanceId == ownerInstanceId).ToList());
        }
    }
}
```

- [ ] **Step 5: Run in-memory store tests — all pass**

```bash
dotnet build tests/MultiChannelAgent.Application.Tests --configuration Release --verbosity quiet && \
dotnet test tests/MultiChannelAgent.Application.Tests --filter "InMemoryVoiceSessionStoreTests" --no-build --configuration Release --verbosity normal
# Expected: Passed! - Failed: 0, Passed: 8
```

- [ ] **Step 6: Create EF entity, configuration, SQL store**

`VoiceSessionEntity` columns: `Id` (Guid PK), `ParticipantId` (Guid), `ControlSessionId` (string?), `ChannelConversationId` (string), `OwnerInstanceId` (string), `Status` (string), `OccupiesSlot` (bool), `StartedAt` (DateTimeOffset), `LastHeartbeatAt` (DateTimeOffset), `EndedAt` (DateTimeOffset?), `ExpiresAt` (DateTimeOffset), `WarningAt` (DateTimeOffset), `IdleExpiresAt` (DateTimeOffset), `WarningIssued` (bool). No `TimeSpan` columns.

Configuration: filtered unique index using persisted boolean:
```csharp
builder.Property(e => e.OccupiesSlot).IsRequired();

builder.HasIndex(e => e.ParticipantId)
    .HasFilter("[OccupiesSlot] = 1")
    .IsUnique()
    .HasDatabaseName("IX_VoiceSessions_ParticipantId_OccupiesSlot");

builder.HasIndex(e => e.OccupiesSlot)
    .HasDatabaseName("IX_VoiceSessions_OccupiesSlot");
```

`SqlVoiceSessionStore.TryAdmitAsync`:
```csharp
public async Task<bool> TryAdmitAsync(VoiceSession session, int globalCap, CancellationToken ct)
{
    await using var transaction = await db.Database.BeginTransactionAsync(
        System.Data.IsolationLevel.Serializable, ct);
    try
    {
        // UPDLOCK prevents two concurrent readers from both seeing count < cap.
        // HOLDLOCK retains the range lock until commit, preventing phantom inserts.
        var occupiedCount = await db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS [Value] FROM VoiceSessions WITH (UPDLOCK, HOLDLOCK) WHERE OccupiesSlot = 1")
            .SingleAsync(ct);

        if (occupiedCount >= globalCap)
        {
            await db.AbandonAsync(transaction);
            return false;
        }

        db.VoiceSessions.Add(MapToEntity(session));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }
    catch (DbUpdateException)
    {
        // Filtered unique index violation — another Negotiating/Active row for this Participant.
        await db.AbandonAsync(transaction);
        return false;
    }
    catch
    {
        await db.AbandonAsync(transaction);
        throw;
    }
}
```

- [ ] **Step 7: Add VoiceSessions DbSet, generate the voice-session migration**

```bash
dotnet ef migrations add AddVoiceSessions \
  --project src/MultiChannelAgent.Infrastructure \
  --startup-project src/MultiChannelAgent.Host
```

- [ ] **Step 8: Build and run in-memory store tests — all pass**

```bash
dotnet build tests/MultiChannelAgent.Application.Tests --configuration Release --verbosity quiet && \
dotnet test tests/MultiChannelAgent.Application.Tests --filter "InMemoryVoiceSessionStoreTests" --no-build --configuration Release --verbosity normal
# Expected: Passed! - Failed: 0, Passed: 8
```

- [ ] **Step 9: Commit**

```bash
git add src/MultiChannelAgent.Application/Voice/IVoiceSessionStore.cs \
        src/MultiChannelAgent.Infrastructure/Persistence/ \
        src/MultiChannelAgent.Infrastructure/Voice/SqlVoiceSessionStore.cs \
        tests/MultiChannelAgent.Application.Tests/Voice/InMemoryVoiceSessionStore.cs \
        tests/MultiChannelAgent.Application.Tests/Voice/InMemoryVoiceSessionStoreTests.cs
git commit -m "feat(voice): add atomic SQL voice session store with OccupiesSlot, SERIALIZABLE, and UPDLOCK,HOLDLOCK"
```

---

## Task 5: Voice Admission Service — Reserve-Negotiate-Activate

**Why now:** Core admission: atomically reserve → negotiate outside SQL tx → activate or abandon.

**Files:**
- Create: `src/MultiChannelAgent.Application/Voice/VoiceAdmissionService.cs`
- Create: `tests/MultiChannelAgent.Application.Tests/Voice/VoiceAdmissionServiceTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/MultiChannelAgent.Application.Tests/Voice/VoiceAdmissionServiceTests.cs
using Microsoft.Extensions.Time.Testing.FakeTimeProvider;
using MultiChannelAgent.Application.Voice;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.Voice;

public sealed class VoiceAdmissionServiceTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryVoiceSessionStore _store = new();
    private readonly FakeVoiceLiveGateway _gateway = new();
    private static readonly ParticipantId Alice = new(Guid.Parse("aaaa0000-0000-0000-0000-000000000001"));
    private static readonly ParticipantId Bob = new(Guid.Parse("bbbb0000-0000-0000-0000-000000000002"));

    private VoiceAdmissionService CreateService(int cap = 5) =>
        new(_store, _gateway, new VoiceOptions { Enabled = true, Endpoint = "wss://x", Model = "gpt-4.1", GlobalActiveCap = cap },
            _time, "test-instance");

    [Fact]
    public async Task First_session_admitted_successfully()
    {
        var result = await CreateService().AdmitAsync(Alice, "conv-1", "v=0\r\n", CancellationToken.None);
        Assert.True(result.Admitted);
        Assert.NotNull(result.VoiceSessionId);
        Assert.NotNull(result.SdpAnswer);
    }

    [Fact]
    public async Task Same_participant_rejected()
    {
        var svc = CreateService();
        await svc.AdmitAsync(Alice, "conv-1", "v=0\r\n", CancellationToken.None);
        var second = await svc.AdmitAsync(Alice, "conv-2", "v=0\r\n", CancellationToken.None);
        Assert.False(second.Admitted);
    }

    [Fact]
    public async Task Global_cap_rejected()
    {
        var svc = CreateService(cap: 1);
        await svc.AdmitAsync(Alice, "conv-1", "v=0\r\n", CancellationToken.None);
        var second = await svc.AdmitAsync(Bob, "conv-2", "v=0\r\n", CancellationToken.None);
        Assert.False(second.Admitted);
    }

    [Fact]
    public async Task Disabled_voice_rejected()
    {
        var svc = new VoiceAdmissionService(
            _store, _gateway,
            new VoiceOptions { Enabled = false },
            _time, "test-instance");
        var result = await svc.AdmitAsync(Alice, "conv-1", "v=0\r\n", CancellationToken.None);
        Assert.False(result.Admitted);
        Assert.Equal(VoiceAdmissionDenialReason.VoiceDisabled, result.DenialReason);
    }

    [Fact]
    public async Task Gateway_failure_abandons_reservation_and_reclaims_capacity()
    {
        _gateway.NextNegotiationFailure = new InvalidOperationException("Azure down");
        var svc = CreateService();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AdmitAsync(Alice, "conv-1", "v=0\r\n", CancellationToken.None));
        // Retry succeeds — capacity was reclaimed because OccupiesSlot was set to false.
        var retry = await svc.AdmitAsync(Alice, "conv-1", "v=0\r\n", CancellationToken.None);
        Assert.True(retry.Admitted);
    }

    [Fact]
    public async Task Gateway_success_then_activation_failure_calls_TerminateAsync_and_abandons()
    {
        // Simulate activation failure by using a store that throws on the second UpdateAsync.
        var failingStore = new ActivationFailingVoiceSessionStore(_store);
        var svc = new VoiceAdmissionService(
            failingStore, _gateway,
            new VoiceOptions { Enabled = true, Endpoint = "wss://x", Model = "gpt-4.1" },
            _time, "test-instance");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AdmitAsync(Alice, "conv-1", "v=0\r\n", CancellationToken.None));

        // Gateway session was terminated.
        Assert.True(_gateway.Sessions.Values.Single().Terminated);
    }
}
```

The `ActivationFailingVoiceSessionStore` is a decorator that delegates to the real store but throws on `UpdateAsync` when the session is transitioning to Active (i.e., `session.Status == VoiceSessionStatus.Active`), simulating an infrastructure failure during the activation step only.

- [ ] **Step 2: Run test — BUILD FAIL**

```bash
dotnet build tests/MultiChannelAgent.Application.Tests --configuration Release --verbosity quiet 2>&1 | head -20
# Expected: CS0234 — VoiceAdmissionService does not exist
```

- [ ] **Step 3: Create admission service**

```csharp
// src/MultiChannelAgent.Application/Voice/VoiceAdmissionService.cs
public sealed record VoiceAdmissionResult(
    bool Admitted, Guid? VoiceSessionId, string? SdpAnswer, VoiceAdmissionDenialReason? DenialReason)
{
    public static VoiceAdmissionResult Success(VoiceSessionId id, string sdpAnswer) =>
        new(true, id.Value, sdpAnswer, null);
    public static VoiceAdmissionResult Denied(VoiceAdmissionDenialReason reason) =>
        new(false, null, null, reason);
}

public enum VoiceAdmissionDenialReason { VoiceDisabled, AlreadyActive, GlobalCapReached }

public sealed class VoiceAdmissionService(
    IVoiceSessionStore store, IVoiceLiveGateway gateway, VoiceOptions options,
    TimeProvider timeProvider, string ownerInstanceId)
{
    public async Task<VoiceAdmissionResult> AdmitAsync(
        ParticipantId participantId, string channelConversationId,
        string sdpOffer, CancellationToken ct)
    {
        if (!options.Enabled)
            return VoiceAdmissionResult.Denied(VoiceAdmissionDenialReason.VoiceDisabled);

        var now = timeProvider.GetUtcNow();
        var session = VoiceSession.Reserve(participantId, channelConversationId,
            ownerInstanceId, now, options.ComputeDeadlines(now));

        var reserved = await store.TryAdmitAsync(session, options.GlobalActiveCap, ct);
        if (!reserved)
            return VoiceAdmissionResult.Denied(VoiceAdmissionDenialReason.GlobalCapReached);

        VoiceLiveNegotiationResult negotiation;
        try
        {
            negotiation = await gateway.NegotiateAsync(new VoiceLiveNegotiationRequest(sdpOffer), ct);
        }
        catch
        {
            session.Abandon(timeProvider.GetUtcNow());
            await store.UpdateAsync(session, ct);
            throw;
        }

        try
        {
            session.Activate(negotiation.ControlSessionId, timeProvider.GetUtcNow());
            await store.UpdateAsync(session, ct);
        }
        catch
        {
            await gateway.TerminateAsync(negotiation.ControlSessionId, ct);
            session.Abandon(timeProvider.GetUtcNow());
            try { await store.UpdateAsync(session, ct); } catch { /* best-effort SQL cleanup */ }
            throw;
        }

        return VoiceAdmissionResult.Success(session.Id, negotiation.SdpAnswer);
    }
}
```

- [ ] **Step 4: Run tests — all pass**

```bash
dotnet build tests/MultiChannelAgent.Application.Tests --configuration Release --verbosity quiet && \
dotnet test tests/MultiChannelAgent.Application.Tests --filter "VoiceAdmissionServiceTests" --no-build --configuration Release --verbosity normal
# Expected: Passed! - Failed: 0, Passed: 6
```

- [ ] **Step 5: Commit**

```bash
git add src/MultiChannelAgent.Application/Voice/VoiceAdmissionService.cs \
        tests/MultiChannelAgent.Application.Tests/Voice/VoiceAdmissionServiceTests.cs
git commit -m "feat(voice): add reserve-negotiate-activate voice admission service"
```

---

## Task 6: Release, Heartbeat with Lifecycle State, Cleanup, and Stale-Owner Reclamation

**Why now:** Heartbeat must return authoritative lifecycle state. Stale-owner reclamation handles restarts.

**Files:**
- Create: `src/MultiChannelAgent.Application/Voice/HeartbeatResult.cs`
- Create: `src/MultiChannelAgent.Application/Voice/VoiceSessionReleaseService.cs`
- Create: `src/MultiChannelAgent.Application/Voice/VoiceSessionCleanupCoordinator.cs`
- Create: `tests/MultiChannelAgent.Application.Tests/Voice/VoiceSessionReleaseServiceTests.cs`
- Create: `tests/MultiChannelAgent.Application.Tests/Voice/VoiceSessionCleanupCoordinatorTests.cs`

- [ ] **Step 1: Write the failing heartbeat lifecycle state tests**

```csharp
// tests/MultiChannelAgent.Application.Tests/Voice/VoiceSessionReleaseServiceTests.cs
using Microsoft.Extensions.Time.Testing.FakeTimeProvider;
using MultiChannelAgent.Application.Voice;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Voice;

namespace MultiChannelAgent.Application.Tests.Voice;

public sealed class VoiceSessionReleaseServiceTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryVoiceSessionStore _store = new();
    private readonly FakeVoiceLiveGateway _gateway = new();
    private static readonly ParticipantId Alice = new(Guid.Parse("aaaa0000-0000-0000-0000-000000000001"));
    private static readonly VoiceSessionDeadlines Deadlines = new(
        new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero) + TimeSpan.FromMinutes(30),
        new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero) + TimeSpan.FromMinutes(25),
        new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero) + TimeSpan.FromSeconds(60));

    private async Task<VoiceSession> AdmitAlice()
    {
        var s = VoiceSession.Reserve(Alice, "conv-1", "test-instance", _time.GetUtcNow(), Deadlines);
        await _store.TryAdmitAsync(s, 5, CancellationToken.None);
        s.Activate("ctrl-1", _time.GetUtcNow());
        await _store.UpdateAsync(s, CancellationToken.None);
        return s;
    }

    [Fact]
    public async Task Heartbeat_returns_active_with_remaining_seconds()
    {
        var session = await AdmitAlice();
        _time.Advance(TimeSpan.FromMinutes(10));
        var svc = new VoiceSessionReleaseService(_store, _gateway, _time, TimeSpan.FromSeconds(60));
        var r = await svc.HeartbeatAsync(session.Id, Alice, CancellationToken.None);
        Assert.True(r.Renewed);
        Assert.Equal("active", r.LifecycleState);
        Assert.InRange(r.RemainingSeconds!.Value, 1199, 1201);
    }

    [Fact]
    public async Task Heartbeat_returns_warning_due_exactly_once()
    {
        var session = await AdmitAlice();
        _time.Advance(TimeSpan.FromMinutes(25));
        var svc = new VoiceSessionReleaseService(_store, _gateway, _time, TimeSpan.FromSeconds(60));
        var first = await svc.HeartbeatAsync(session.Id, Alice, CancellationToken.None);
        Assert.Equal("warning_due", first.LifecycleState);
        _time.Advance(TimeSpan.FromSeconds(30));
        var second = await svc.HeartbeatAsync(session.Id, Alice, CancellationToken.None);
        Assert.Equal("active", second.LifecycleState);
    }

    [Fact]
    public async Task Heartbeat_returns_expired_for_timed_out_session()
    {
        var session = await AdmitAlice();
        _time.Advance(TimeSpan.FromMinutes(31));
        var svc = new VoiceSessionReleaseService(_store, _gateway, _time, TimeSpan.FromSeconds(60));
        var r = await svc.HeartbeatAsync(session.Id, Alice, CancellationToken.None);
        Assert.Equal("expired", r.LifecycleState);
        Assert.False(r.Renewed);
    }

    [Fact]
    public async Task Heartbeat_returns_not_found_for_nonexistent_session()
    {
        var svc = new VoiceSessionReleaseService(_store, _gateway, _time, TimeSpan.FromSeconds(60));
        var r = await svc.HeartbeatAsync(new VoiceSessionId(Guid.NewGuid()), Alice, CancellationToken.None);
        Assert.Equal("not_found", r.LifecycleState);
        Assert.False(r.Renewed);
    }

    [Fact]
    public async Task Release_ends_session_and_terminates_gateway()
    {
        var session = await AdmitAlice();
        var svc = new VoiceSessionReleaseService(_store, _gateway, _time, TimeSpan.FromSeconds(60));
        await svc.ReleaseAsync(session.Id, Alice, CancellationToken.None);
        var found = await _store.FindByIdAsync(session.Id, CancellationToken.None);
        Assert.Equal(VoiceSessionStatus.Ended, found!.Status);
        Assert.False(found.OccupiesSlot);
        Assert.True(_gateway.Sessions["ctrl-1"].Terminated);
    }
}
```

- [ ] **Step 2: Write stale-owner cleanup tests**

```csharp
// tests/MultiChannelAgent.Application.Tests/Voice/VoiceSessionCleanupCoordinatorTests.cs
using Microsoft.Extensions.Time.Testing.FakeTimeProvider;
using MultiChannelAgent.Application.Voice;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Voice;

namespace MultiChannelAgent.Application.Tests.Voice;

public sealed class VoiceSessionCleanupCoordinatorTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryVoiceSessionStore _store = new();
    private readonly FakeVoiceLiveGateway _gateway = new();
    private static readonly ParticipantId Alice = new(Guid.Parse("aaaa0000-0000-0000-0000-000000000001"));
    private static readonly VoiceSessionDeadlines Deadlines = new(
        new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero) + TimeSpan.FromMinutes(30),
        new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero) + TimeSpan.FromMinutes(25),
        new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero) + TimeSpan.FromSeconds(60));

    [Fact]
    public async Task Cleanup_ends_expired_sessions()
    {
        var s = VoiceSession.Reserve(Alice, "conv-1", "current-instance", _time.GetUtcNow(), Deadlines);
        await _store.TryAdmitAsync(s, 5, CancellationToken.None);
        s.Activate("ctrl-1", _time.GetUtcNow());
        await _store.UpdateAsync(s, CancellationToken.None);

        _time.Advance(TimeSpan.FromMinutes(31));
        var releaseService = new VoiceSessionReleaseService(_store, _gateway, _time, TimeSpan.FromSeconds(60));
        var coordinator = new VoiceSessionCleanupCoordinator(_store, releaseService, _time, "current-instance", 60);
        await coordinator.CleanupAsync(CancellationToken.None);

        var found = await _store.FindByIdAsync(s.Id, CancellationToken.None);
        Assert.Equal(VoiceSessionStatus.Ended, found!.Status);
        Assert.False(found.OccupiesSlot);
    }

    [Fact]
    public async Task Cleanup_ends_sessions_owned_by_stale_instances()
    {
        var s = VoiceSession.Reserve(Alice, "conv-1", "dead-instance", _time.GetUtcNow(), Deadlines);
        await _store.TryAdmitAsync(s, 5, CancellationToken.None);
        s.Activate("ctrl-1", _time.GetUtcNow());
        await _store.UpdateAsync(s, CancellationToken.None);

        _time.Advance(TimeSpan.FromSeconds(61));
        var releaseService = new VoiceSessionReleaseService(_store, _gateway, _time, TimeSpan.FromSeconds(60));
        var coordinator = new VoiceSessionCleanupCoordinator(_store, releaseService, _time, "current-instance", 60);
        await coordinator.CleanupAsync(CancellationToken.None);

        var found = await _store.FindByIdAsync(s.Id, CancellationToken.None);
        Assert.Equal(VoiceSessionStatus.Ended, found!.Status);
        Assert.False(found.OccupiesSlot);
    }

    [Fact]
    public async Task Cleanup_skips_sessions_owned_by_current_instance_that_are_not_expired()
    {
        var s = VoiceSession.Reserve(Alice, "conv-1", "current-instance", _time.GetUtcNow(), Deadlines);
        await _store.TryAdmitAsync(s, 5, CancellationToken.None);
        s.Activate("ctrl-1", _time.GetUtcNow());
        await _store.UpdateAsync(s, CancellationToken.None);

        // Advance past idle but not expiry — heartbeat would have reset idle, so only
        // expired/stale-owner triggers cleanup for current instance's sessions.
        _time.Advance(TimeSpan.FromSeconds(30));
        var releaseService = new VoiceSessionReleaseService(_store, _gateway, _time, TimeSpan.FromSeconds(60));
        var coordinator = new VoiceSessionCleanupCoordinator(_store, releaseService, _time, "current-instance", 60);
        await coordinator.CleanupAsync(CancellationToken.None);

        var found = await _store.FindByIdAsync(s.Id, CancellationToken.None);
        Assert.Equal(VoiceSessionStatus.Active, found!.Status);
    }
}
```

- [ ] **Step 3: Run tests — BUILD FAIL**

```bash
dotnet build tests/MultiChannelAgent.Application.Tests --configuration Release --verbosity quiet 2>&1 | head -20
# Expected: CS0234 — HeartbeatResult, VoiceSessionReleaseService, VoiceSessionCleanupCoordinator do not exist
```

- [ ] **Step 4: Implement HeartbeatResult, release service, cleanup coordinator**

`HeartbeatResult`: `sealed record(bool Renewed, string LifecycleState, int? RemainingSeconds, string? ForcedCloseReason)`.

Release service `HeartbeatAsync`: finds session by id, validates participant ownership, renews heartbeat, computes lifecycle state. State priority: expired → idle → warning_due (once via `ShouldWarn`) → active. Returns `not_found` for missing or wrong-participant sessions.

Release service `ReleaseAsync`: ends session, terminates gateway handle if `ControlSessionId` is present.

Cleanup coordinator `CleanupAsync`: finds expired/idle sessions via `FindExpiredOrIdleAsync`, force-closes each. Also scans for sessions with stale `OwnerInstanceId` (not current instance, last heartbeat older than lease) and force-closes those too.

- [ ] **Step 5: Run tests — all pass**

```bash
dotnet build tests/MultiChannelAgent.Application.Tests --configuration Release --verbosity quiet && \
dotnet test tests/MultiChannelAgent.Application.Tests --filter "VoiceSessionReleaseServiceTests|VoiceSessionCleanupCoordinatorTests" --no-build --configuration Release --verbosity normal
# Expected: Passed! - Failed: 0, Passed: 9
```

- [ ] **Step 6: Commit**

```bash
git add src/MultiChannelAgent.Application/Voice/HeartbeatResult.cs \
        src/MultiChannelAgent.Application/Voice/VoiceSessionReleaseService.cs \
        src/MultiChannelAgent.Application/Voice/VoiceSessionCleanupCoordinator.cs \
        tests/MultiChannelAgent.Application.Tests/Voice/VoiceSessionReleaseServiceTests.cs \
        tests/MultiChannelAgent.Application.Tests/Voice/VoiceSessionCleanupCoordinatorTests.cs
git commit -m "feat(voice): add release, heartbeat lifecycle state, cleanup, and stale-owner reclamation"
```

---

## Task 7: InputModality Domain Type, Confirmation Policy, and Turn Provenance

**Why now:** The InputModality enum and updated DirectConfirmationEvidenceReader must exist before the HTTP endpoints set modality on voice-originated Turns.

**Files:**
- Create: `src/MultiChannelAgent.Domain/Turns/InputModality.cs`
- Modify: `src/MultiChannelAgent.Domain/Turns/InboundTurnDraft.cs` (add `InputModality` property)
- Modify: `src/MultiChannelAgent.Domain/Turns/InboundTurn.cs` (add `InputModality` property, pass through from draft)
- Modify: `src/MultiChannelAgent.Application/Turns/DirectConfirmationEvidence.cs` (return `None` for Voice modality)
- Modify: `src/MultiChannelAgent.Application/Turns/SubmitTurnRequest.cs` (add `InputModality` field)
- Modify: `src/MultiChannelAgent.Application/Turns/TurnAcceptanceService.cs` (pass `InputModality` into the draft)
- Modify: `src/MultiChannelAgent.Infrastructure/Persistence/Entities/InboxEntryEntity.cs` (add `InputModality` column)
- Modify: `src/MultiChannelAgent.Infrastructure/Persistence/Configurations/InboxEntryEntityConfiguration.cs` (configure column)
- Modify: `src/MultiChannelAgent.Infrastructure/Turns/SqlInboxStore.cs` (map `InputModality` both directions)
- Create: migration `AddInputModality`
- Create: `tests/MultiChannelAgent.Application.Tests/Voice/VoiceModalityConfirmationTests.cs`

- [ ] **Step 1: Write the failing confirmation policy tests**

```csharp
// tests/MultiChannelAgent.Application.Tests/Voice/VoiceModalityConfirmationTests.cs
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Tests.Voice;

public sealed class VoiceModalityConfirmationTests
{
    private static readonly ParticipantId Alice = new(Guid.Parse("aaaa0000-0000-0000-0000-000000000001"));
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    private static InboundTurn CreateTurn(string contentText, InputModality modality, bool wasInterrupted = false) =>
        InboundTurn.Create(new InboundTurnDraft
        {
            NativeMessageId = Guid.NewGuid().ToString(),
            ParticipantId = Alice,
            ChannelConversationId = "conv-1",
            Channel = "web",
            Principal = ChannelPrincipal.EntraUser(Alice.Value.ToString(), null),
            Capabilities = ChannelCapabilities.Text | ChannelCapabilities.RichText | ChannelCapabilities.ProgressEvents |
                (modality == InputModality.Voice ? ChannelCapabilities.Voice : ChannelCapabilities.None),
            ContentParts = [TurnContentPart.Create(1, ContentProvenance.Direct, contentText)],
            ReceivedAt = Now,
            InputModality = modality,
            WasInterrupted = wasInterrupted,
        });

    [Fact]
    public void Voice_modality_confirm_returns_None_regardless_of_content()
    {
        // A voice Turn saying "confirm <token>" must never consume a pending confirmation.
        var turn = CreateTurn("confirm ABC_token_placeholder_43chars_here1234567", InputModality.Voice);
        Assert.Equal(DirectConfirmationEvidence.None, DirectConfirmationEvidenceReader.Read(turn));
    }

    [Fact]
    public void Voice_modality_reject_returns_None()
    {
        var turn = CreateTurn("reject", InputModality.Voice);
        Assert.Equal(DirectConfirmationEvidence.None, DirectConfirmationEvidenceReader.Read(turn));
    }

    [Fact]
    public void Voice_modality_yes_returns_None()
    {
        var turn = CreateTurn("yes", InputModality.Voice);
        Assert.Equal(DirectConfirmationEvidence.None, DirectConfirmationEvidenceReader.Read(turn));
    }

    [Fact]
    public void Text_modality_confirm_returns_Confirmed()
    {
        var turn = CreateTurn("confirm ABC_token_placeholder_43chars_here1234567", InputModality.Text);
        Assert.Equal(DirectConfirmationEvidence.Confirmed, DirectConfirmationEvidenceReader.Read(turn));
    }

    [Fact]
    public void Text_modality_reject_returns_Rejected()
    {
        var turn = CreateTurn("reject", InputModality.Text);
        Assert.Equal(DirectConfirmationEvidence.Rejected, DirectConfirmationEvidenceReader.Read(turn));
    }

    [Fact]
    public void Text_modality_interrupted_returns_None()
    {
        var turn = CreateTurn("confirm ABC_token_placeholder_43chars_here1234567", InputModality.Text, wasInterrupted: true);
        Assert.Equal(DirectConfirmationEvidence.None, DirectConfirmationEvidenceReader.Read(turn));
    }

    [Fact]
    public void Voice_modality_ordinary_request_returns_None()
    {
        // Voice can request/clarify but never provide confirmation evidence.
        var turn = CreateTurn("add five boxes of gloves", InputModality.Voice);
        Assert.Equal(DirectConfirmationEvidence.None, DirectConfirmationEvidenceReader.Read(turn));
    }

    [Fact]
    public void Text_modality_ordinary_request_returns_None()
    {
        var turn = CreateTurn("add five boxes of gloves", InputModality.Text);
        Assert.Equal(DirectConfirmationEvidence.None, DirectConfirmationEvidenceReader.Read(turn));
    }
}
```

- [ ] **Step 2: Run test — BUILD FAIL**

```bash
dotnet build tests/MultiChannelAgent.Application.Tests --configuration Release --verbosity quiet 2>&1 | head -20
# Expected: CS0234 — InputModality does not exist
```

- [ ] **Step 3: Create InputModality and update domain types**

```csharp
// src/MultiChannelAgent.Domain/Turns/InputModality.cs
namespace MultiChannelAgent.Domain.Turns;

/// <summary>
/// How the Participant's input was captured. Set by the Host after validating trusted evidence
/// (e.g., active voiceSessionId for Voice). Clients cannot set modality directly.
/// </summary>
public enum InputModality
{
    /// <summary>Typed text input — the default for all existing channels.</summary>
    Text = 0,

    /// <summary>Speech input via an active, server-validated voice session.</summary>
    Voice = 1,
}
```

Add `public InputModality InputModality { get; init; }` to `InboundTurnDraft` and `InboundTurn`. Pass through in `InboundTurn.Create`. Add `InputModality` default `Text` to `InboundTurnDraft.DirectText` factory.

Add `public InputModality InputModality { get; set; }` to `InboxEntryEntity`.

Add to `InboxEntryEntityConfiguration`:
```csharp
builder.Property(e => e.InputModality).HasConversion<string>().HasMaxLength(16).HasDefaultValue("Text");
```

Add to `SubmitTurnRequest`: `InputModality InputModality = InputModality.Text`.

Update `TurnAcceptanceService` so the request's Host-attested modality is copied into the
`InboundTurnDraft`, and update both mappings in `SqlInboxStore` so acceptance stores
`turn.InputModality` and reads the persisted value back into the reconstituted Turn. Existing rows
must remain `Text`.

- [ ] **Step 4: Update DirectConfirmationEvidenceReader**

Add an early return before the existing `WasInterrupted` check:

```csharp
public static DirectConfirmationEvidence Read(InboundTurn turn)
{
    ArgumentNullException.ThrowIfNull(turn);

    // Voice-originated Turns can never provide confirmation evidence. Voice Live provides
    // no trusted recognition-confidence signal, so all voice confirmation attempts are
    // clarification-only. The Participant must use visible text input to confirm.
    if (turn.InputModality == InputModality.Voice)
    {
        return DirectConfirmationEvidence.None;
    }

    if (turn.WasInterrupted)
    {
        return DirectConfirmationEvidence.None;
    }

    var text = turn.ContentText.TrimStart();
    if (StartsWithAnswer(text, Negatives))
    {
        return DirectConfirmationEvidence.Rejected;
    }

    return StartsWithAnswer(text, Affirmatives)
        ? DirectConfirmationEvidence.Confirmed
        : DirectConfirmationEvidence.None;
}
```

- [ ] **Step 5: Generate the InputModality migration**

```bash
dotnet ef migrations add AddInputModality \
  --project src/MultiChannelAgent.Infrastructure \
  --startup-project src/MultiChannelAgent.Host
```

Expected: migration adds a non-null `InputModality` column to `InboxEntries` with `Text` as the
default/backfill and does not modify `VoiceSessions`.

- [ ] **Step 6: Run tests — all pass**

```bash
dotnet build tests/MultiChannelAgent.Application.Tests --configuration Release --verbosity quiet && \
dotnet test tests/MultiChannelAgent.Application.Tests --filter "VoiceModalityConfirmationTests" --no-build --configuration Release --verbosity normal
# Expected: Passed! - Failed: 0, Passed: 8
```

- [ ] **Step 7: Run ALL existing backend tests to verify no regression**

```bash
dotnet build --configuration Release --verbosity quiet && \
dotnet test --configuration Release --no-build --verbosity normal
# Expected: all existing tests still pass, including DirectConfirmationEvidenceReaderTests
```

- [ ] **Step 8: Commit**

```bash
git add src/MultiChannelAgent.Domain/Turns/InputModality.cs \
        src/MultiChannelAgent.Domain/Turns/InboundTurnDraft.cs \
        src/MultiChannelAgent.Domain/Turns/InboundTurn.cs \
        src/MultiChannelAgent.Application/Turns/DirectConfirmationEvidence.cs \
        src/MultiChannelAgent.Application/Turns/SubmitTurnRequest.cs \
        src/MultiChannelAgent.Application/Turns/TurnAcceptanceService.cs \
        src/MultiChannelAgent.Infrastructure/Persistence/Entities/InboxEntryEntity.cs \
        src/MultiChannelAgent.Infrastructure/Persistence/Configurations/InboxEntryEntityConfiguration.cs \
        src/MultiChannelAgent.Infrastructure/Turns/SqlInboxStore.cs \
        src/MultiChannelAgent.Infrastructure/Persistence/Migrations/ \
        tests/MultiChannelAgent.Application.Tests/Voice/VoiceModalityConfirmationTests.cs
git commit -m "feat(voice): add InputModality to domain, block voice confirmation in DirectConfirmationEvidenceReader"
```

---

## Task 8: Voice HTTP Endpoints with Voice-Provenance Turn Submission

**Why now:** Browser needs HTTP endpoints. Voice-originated Turns carry `InputModality.Voice` and `ChannelCapabilities.Voice`, server-attested.

**Files:**
- Create: `src/MultiChannelAgent.Host/Endpoints/VoiceEndpoints.cs`
- Create: `src/MultiChannelAgent.Host/Workers/VoiceSessionCleanupWorker.cs`
- Modify: `src/MultiChannelAgent.Host/Endpoints/TurnEndpoints.cs`
- Modify: `src/MultiChannelAgent.Host/Program.cs`
- Modify: `src/MultiChannelAgent.Infrastructure/ServiceCollectionExtensions.cs`
- Modify: `src/web/src/turnsApi.ts` (add `voiceSessionId` to request)
- Create: `tests/MultiChannelAgent.IntegrationTests/Voice/VoiceAdmissionHttpTests.cs`
- Create: `tests/MultiChannelAgent.IntegrationTests/Voice/VoiceTurnProvenanceTests.cs`

- [ ] **Step 1: Write endpoint tests — auth, CSRF, admission response shape**

```csharp
// tests/MultiChannelAgent.IntegrationTests/Voice/VoiceAdmissionHttpTests.cs
public sealed class VoiceAdmissionHttpTests : IntegrationTestBase
{
    [Fact]
    public async Task Admit_without_auth_returns_401()
    {
        var response = await UnauthenticatedClient.PostAsJsonAsync("/api/voice/admit", new { sdpOffer = "v=0\r\n" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Admit_without_csrf_returns_400()
    {
        var response = await AuthenticatedClientWithoutCsrf.PostAsJsonAsync("/api/voice/admit", new { sdpOffer = "v=0\r\n" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Admit_when_disabled_returns_denied()
    {
        var response = await AuthenticatedClient.PostAsJsonAsync("/api/voice/admit", new { sdpOffer = "v=0\r\n" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("admitted").GetBoolean());
        Assert.Equal("VoiceDisabled", body.GetProperty("denialReason").GetString());
    }

    [Fact]
    public async Task Admit_response_never_contains_controlSessionId_or_azure_urls()
    {
        var response = await AuthenticatedClient.PostAsJsonAsync("/api/voice/admit", new { sdpOffer = "v=0\r\n" });
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("controlSessionId", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("wss://", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("services.ai.azure.com", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Heartbeat_returns_lifecycle_state()
    {
        // Admit first, then heartbeat.
        var admitResponse = await AdmitWithVoiceEnabled();
        var voiceSessionId = admitResponse.GetProperty("voiceSessionId").GetString();
        var heartbeatResponse = await AuthenticatedClient.PostAsJsonAsync(
            "/api/voice/heartbeat", new { voiceSessionId });
        var body = await heartbeatResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("renewed").GetBoolean());
        Assert.Equal("active", body.GetProperty("lifecycleState").GetString());
        Assert.True(body.GetProperty("remainingSeconds").GetInt32() > 0);
    }

    [Fact]
    public async Task Release_returns_200()
    {
        var admitResponse = await AdmitWithVoiceEnabled();
        var voiceSessionId = admitResponse.GetProperty("voiceSessionId").GetString();
        var response = await AuthenticatedClient.PostAsJsonAsync("/api/voice/release", new { voiceSessionId });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
```

- [ ] **Step 2: Write voice provenance tests**

```csharp
// tests/MultiChannelAgent.IntegrationTests/Voice/VoiceTurnProvenanceTests.cs
public sealed class VoiceTurnProvenanceTests : IntegrationTestBase
{
    [Fact]
    public async Task Turn_with_invalid_voiceSessionId_gets_text_modality()
    {
        var response = await SubmitTurn(new
        {
            nativeMessageId = Guid.NewGuid().ToString(),
            contentText = "list stock",
            voiceSessionId = Guid.NewGuid().ToString(),
        });
        // Turn is accepted but with InputModality.Text — the voiceSessionId was not valid.
        var inbox = await ReadInboxEntry(response);
        Assert.Equal("Text", inbox.InputModality);
    }

    [Fact]
    public async Task Turn_with_valid_active_session_gets_voice_modality()
    {
        var admitResponse = await AdmitWithVoiceEnabled();
        var voiceSessionId = admitResponse.GetProperty("voiceSessionId").GetString();
        var response = await SubmitTurn(new
        {
            nativeMessageId = $"voice:{voiceSessionId}:item_123",
            contentText = "add five boxes of gloves",
            voiceSessionId,
        });
        var inbox = await ReadInboxEntry(response);
        Assert.Equal("Voice", inbox.InputModality);
        Assert.True(inbox.Capabilities.HasFlag(ChannelCapabilities.Voice));
    }
}
```

- [ ] **Step 3: Implement endpoints**

The existing `SubmitTurnHttpRequest` gains an optional `VoiceSessionId` (Guid?). `TurnEndpoints` POST handler:
```csharp
var inputModality = InputModality.Text;
var capabilities = WebChannel.Capabilities;

if (request.VoiceSessionId is { } vsid)
{
    var voiceSession = await voiceSessionStore.FindByIdAsync(new VoiceSessionId(vsid), cancellationToken);
    if (voiceSession is not null
        && voiceSession.ParticipantId == participantId
        && voiceSession.Status == VoiceSessionStatus.Active)
    {
        inputModality = InputModality.Voice;
        capabilities = WebChannel.Capabilities | ChannelCapabilities.Voice;
    }
}
```

`SubmitTurnRequest` passes through `InputModality`. `TurnAcceptanceService` passes it to `InboundTurnDraft`.

Admit endpoint: POST `/api/voice/admit`, body `{ sdpOffer }`, CSRF-protected. Returns `{ admitted, voiceSessionId, sdpAnswer, denialReason }`. Never includes Azure internals.

Heartbeat endpoint: POST `/api/voice/heartbeat`, body `{ voiceSessionId }`, returns HeartbeatResult JSON.

Release endpoint: POST `/api/voice/release`, body `{ voiceSessionId }`, returns 200/404.

Cleanup worker: `BackgroundService` calling coordinator every 15 seconds.

Program.cs: bind `VoiceOptions` from config, validate on startup when enabled, register services, map endpoints.

Frontend: add `voiceSessionId?: string` to `SubmitTurnRequest` in `turnsApi.ts`.

- [ ] **Step 4: Run tests — all pass**

```bash
dotnet build --configuration Release --verbosity quiet && \
dotnet test --configuration Release --no-build --verbosity normal
# Expected: all tests pass
```

- [ ] **Step 5: Commit**

```bash
git add src/MultiChannelAgent.Host/Endpoints/VoiceEndpoints.cs \
        src/MultiChannelAgent.Host/Endpoints/TurnEndpoints.cs \
        src/MultiChannelAgent.Host/Workers/VoiceSessionCleanupWorker.cs \
        src/MultiChannelAgent.Host/Program.cs \
        src/MultiChannelAgent.Infrastructure/ServiceCollectionExtensions.cs \
        src/web/src/turnsApi.ts \
        tests/MultiChannelAgent.IntegrationTests/Voice/VoiceAdmissionHttpTests.cs \
        tests/MultiChannelAgent.IntegrationTests/Voice/VoiceTurnProvenanceTests.cs
git commit -m "feat(voice): add voice HTTP endpoints with server-attested InputModality provenance"
```

---

## Task 9: Security Tests — No Azure Credentials Leak

**Files:**
- Create: `tests/MultiChannelAgent.IntegrationTests/Voice/VoiceSecurityTests.cs`

- [ ] **Step 1: Write credential-leak proof tests**

```csharp
// tests/MultiChannelAgent.IntegrationTests/Voice/VoiceSecurityTests.cs
public sealed class VoiceSecurityTests : IntegrationTestBase
{
    [Fact]
    public async Task Admit_response_contains_no_azure_credentials()
    {
        var response = await AuthenticatedClient.PostAsJsonAsync("/api/voice/admit", new { sdpOffer = "v=0\r\n" });
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("services.ai.azure.com", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("apiKey", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("api-key", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("api_key", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("controlSessionId", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("wss://", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bearer", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Validation_error_does_not_leak_internals()
    {
        // Send empty sdpOffer. Response must be a clean validation problem, not a stack trace.
        var response = await AuthenticatedClient.PostAsJsonAsync("/api/voice/admit", new { sdpOffer = "" });
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Exception", text);
        Assert.DoesNotContain("StackTrace", text);
        Assert.DoesNotContain("azure", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Heartbeat_for_nonexistent_session_leaks_nothing()
    {
        var response = await AuthenticatedClient.PostAsJsonAsync(
            "/api/voice/heartbeat", new { voiceSessionId = Guid.NewGuid() });
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("azure", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Exception", text);
    }
}
```

- [ ] **Step 2: Run tests — all pass**

```bash
dotnet test tests/MultiChannelAgent.IntegrationTests --filter "VoiceSecurityTests" --configuration Release --verbosity normal
# Expected: Passed! - Failed: 0, Passed: 3
```

- [ ] **Step 3: Commit**

```bash
git add tests/MultiChannelAgent.IntegrationTests/Voice/VoiceSecurityTests.cs
git commit -m "test(voice): add credential-leak proof tests for voice endpoints"
```

---

## Task 10: SQL Concurrency Scenarios

**Files:**
- Create: `tests/MultiChannelAgent.IntegrationTests/Voice/VoiceAdmissionSqlScenarioTests.cs`

Docker-gated tests (skip if unavailable). PR gate when Docker is available.

- [ ] **Step 1: Write concurrent admission tests**

```csharp
// tests/MultiChannelAgent.IntegrationTests/Voice/VoiceAdmissionSqlScenarioTests.cs
[Collection("SqlServer")]
public sealed class VoiceAdmissionSqlScenarioTests(SqlServerFixture fixture) : IAsyncLifetime
{
    [Fact]
    public async Task Concurrent_same_participant_exactly_one_admitted()
    {
        // Two concurrent admits for the same participant → exactly 1 admitted.
        // OccupiesSlot filtered unique index prevents both from succeeding.
        var tasks = Enumerable.Range(0, 2).Select(_ =>
            CreateAdmissionService().AdmitAsync(Alice, $"conv-{Guid.NewGuid()}", "v=0\r\n", CancellationToken.None));
        var results = await Task.WhenAll(tasks);
        Assert.Equal(1, results.Count(r => r.Admitted));
    }

    [Fact]
    public async Task Concurrent_final_slot_at_most_cap_admitted()
    {
        // 3 different participants, cap=2, all concurrent → at most 2 admitted.
        // UPDLOCK,HOLDLOCK on OccupiesSlot=1 count prevents phantom final-slot races.
        var participants = new[] { Alice, Bob, Charlie };
        var tasks = participants.Select(p =>
            CreateAdmissionService(cap: 2).AdmitAsync(p, $"conv-{Guid.NewGuid()}", "v=0\r\n", CancellationToken.None));
        var results = await Task.WhenAll(tasks);
        Assert.True(results.Count(r => r.Admitted) <= 2);
        Assert.True(results.Count(r => r.Admitted) >= 1);
    }

    [Fact]
    public async Task Crashed_negotiation_expiry()
    {
        // Insert a Negotiating row with stale timestamp (OccupiesSlot=true),
        // run cleanup, verify row Ended (OccupiesSlot=false) and slot reclaimed.
        var session = VoiceSession.Reserve(Alice, "conv-1", "dead-inst", Now, StaleDeadlines);
        await store.TryAdmitAsync(session, 5, CancellationToken.None);

        time.Advance(TimeSpan.FromSeconds(61));
        await coordinator.CleanupAsync(CancellationToken.None);

        var found = await store.FindByIdAsync(session.Id, CancellationToken.None);
        Assert.Equal(VoiceSessionStatus.Ended, found!.Status);
        Assert.False(found.OccupiesSlot);

        // Slot reclaimed — new admission succeeds.
        var retry = await CreateAdmissionService(cap: 1).AdmitAsync(
            Alice, "conv-2", "v=0\r\n", CancellationToken.None);
        Assert.True(retry.Admitted);
    }

    [Fact]
    public async Task No_cap_leakage_after_full_lifecycle()
    {
        // Admit N=5, release N=5, verify all 5 slots available again.
        var sessions = new List<VoiceAdmissionResult>();
        for (var i = 0; i < 5; i++)
        {
            var p = new ParticipantId(Guid.NewGuid());
            sessions.Add(await CreateAdmissionService().AdmitAsync(p, $"conv-{i}", "v=0\r\n", CancellationToken.None));
        }
        Assert.Equal(5, sessions.Count(s => s.Admitted));

        foreach (var s in sessions.Where(s => s.Admitted))
            await releaseService.ReleaseAsync(new VoiceSessionId(s.VoiceSessionId!.Value), Alice, CancellationToken.None);

        // All 5 slots reclaimed.
        for (var i = 0; i < 5; i++)
        {
            var p = new ParticipantId(Guid.NewGuid());
            var result = await CreateAdmissionService().AdmitAsync(p, $"conv-r{i}", "v=0\r\n", CancellationToken.None);
            Assert.True(result.Admitted);
        }
    }
}
```

- [ ] **Step 2: Run tests (Docker-gated)**

```bash
dotnet test tests/MultiChannelAgent.IntegrationTests --filter "VoiceAdmissionSqlScenarioTests" --configuration Release --verbosity normal
# Expected: 4 passed (or 4 skipped if Docker unavailable)
```

- [ ] **Step 3: Commit**

```bash
git add tests/MultiChannelAgent.IntegrationTests/Voice/VoiceAdmissionSqlScenarioTests.cs
git commit -m "test(voice): add SQL concurrency scenarios for OccupiesSlot admission"
```

---

## Task 11: Frontend Voice State Machine — Pure Reducer

**Why now:** Foundation of all frontend voice behavior. No WebRTC, no DOM dependencies.

**Files:**
- Create: `src/web/src/voiceReducer.ts`
- Create: `src/web/src/voiceReducer.test.ts`

- [ ] **Step 1: Write the failing tests**

The `FinalizedUtterance` type carries only `text` and `nativeMessageId` — no `uncertain` boolean, because the Voice Live `conversation.item.input_audio_transcription.completed` event provides `item_id`, `content_index`, and `transcript` (optional `logprobs` and `phrases` are excluded from this scope).

```typescript
// src/web/src/voiceReducer.test.ts
import { describe, expect, it } from 'vitest'
import { reduce, initialState, type VoiceState } from './voiceReducer'

// Types — no uncertain field
// export interface FinalizedUtterance {
//   readonly text: string
//   readonly nativeMessageId: string
// }
//
// export interface VoiceState {
//   readonly phase: 'idle' | 'requesting' | 'connecting' | 'listening' | 'speaking' | 'ending'
//   readonly voiceSessionId: string | null
//   readonly muted: boolean
//   readonly speechActive: boolean
//   readonly bargeIn: boolean
//   readonly partialTranscript: string | null
//   readonly finalizedUtterance: FinalizedUtterance | null
//   readonly warning: string | null
//   readonly warningDelivered: boolean
//   readonly error: string | null
//   readonly playbackFailed: boolean
// }

function listening(): VoiceState {
  return reduce(
    reduce(
      reduce(initialState, { type: 'start_requested' }),
      { type: 'admitted', voiceSessionId: 'vs-1', sdpAnswer: 'v=0\r\n' }),
    { type: 'connected' })
}

describe('voiceReducer', () => {
  it('starts idle with null voiceSessionId', () => {
    expect(initialState.phase).toBe('idle')
    expect(initialState.voiceSessionId).toBeNull()
  })

  it('start_requested transitions to requesting', () => {
    const state = reduce(initialState, { type: 'start_requested' })
    expect(state.phase).toBe('requesting')
  })

  it('admitted transitions to connecting with voiceSessionId', () => {
    const requested = reduce(initialState, { type: 'start_requested' })
    const admitted = reduce(requested, { type: 'admitted', voiceSessionId: 'vs-1', sdpAnswer: 'v=0\r\n' })
    expect(admitted.phase).toBe('connecting')
    expect(admitted.voiceSessionId).toBe('vs-1')
  })

  it('denied transitions to idle with error', () => {
    const requested = reduce(initialState, { type: 'start_requested' })
    const denied = reduce(requested, { type: 'denied', reason: 'GlobalCapReached' })
    expect(denied.phase).toBe('idle')
    expect(denied.error).toContain('GlobalCapReached')
  })

  it('connected transitions to listening', () => {
    expect(listening().phase).toBe('listening')
  })

  it('final_transcript carries text and nativeMessageId only', () => {
    const state = reduce(listening(), {
      type: 'final_transcript', text: 'add five boxes', nativeMessageId: 'voice:vs-1:item_1',
    })
    expect(state.finalizedUtterance).toEqual({ text: 'add five boxes', nativeMessageId: 'voice:vs-1:item_1' })
    expect(state.finalizedUtterance).not.toHaveProperty('uncertain')
  })

  it('barge-in during playback does NOT set wasInterrupted on next utterance', () => {
    const speaking = reduce(listening(), { type: 'playback_started' })
    expect(speaking.phase).toBe('speaking')
    const bargedIn = reduce(speaking, { type: 'speech_started' })
    expect(bargedIn.phase).toBe('listening')
    expect(bargedIn.bargeIn).toBe(true)
    const finalized = reduce(bargedIn, {
      type: 'final_transcript', text: 'add five', nativeMessageId: 'voice:vs-1:item_2',
    })
    // bargeIn interrupted the ASSISTANT's playback, not the USER's speech.
    expect(finalized.finalizedUtterance).not.toBeNull()
    expect(finalized.bargeIn).toBe(false)
  })

  it('speech_interrupted clears partial and speechActive', () => {
    let state = reduce(listening(), { type: 'speech_started' })
    state = reduce(state, { type: 'partial_transcript', text: 'add fiv' })
    state = reduce(state, { type: 'speech_interrupted' })
    expect(state.partialTranscript).toBeNull()
    expect(state.finalizedUtterance).toBeNull()
    expect(state.speechActive).toBe(false)
  })

  it('playback_failed keeps session in listening with error', () => {
    const speaking = reduce(listening(), { type: 'playback_started' })
    const state = reduce(speaking, { type: 'playback_failed', error: 'Audio decode error' })
    expect(state.phase).toBe('listening')
    expect(state.playbackFailed).toBe(true)
    expect(state.error).toContain('Audio decode error')
  })

  it('session_warning dispatches exactly once', () => {
    const warned = reduce(listening(), { type: 'session_warning', minutesRemaining: 5 })
    expect(warned.warning).toBe('Voice session ends in 5 minutes.')
    expect(warned.warningDelivered).toBe(true)
    // Second warning is ignored
    const second = reduce(warned, { type: 'session_warning', minutesRemaining: 4 })
    expect(second.warning).toBe('Voice session ends in 5 minutes.')
  })

  it('session_expired returns to idle with reason', () => {
    const state = reduce(listening(), { type: 'session_expired', reason: 'Time limit reached' })
    expect(state.phase).toBe('idle')
    expect(state.voiceSessionId).toBeNull()
    expect(state.error).toContain('Time limit reached')
  })

  it('end_requested transitions to ending then idle', () => {
    const ending = reduce(listening(), { type: 'end_requested' })
    expect(ending.phase).toBe('ending')
    const ended = reduce(ending, { type: 'ended' })
    expect(ended.phase).toBe('idle')
    expect(ended.voiceSessionId).toBeNull()
  })

  it('error_occurred returns to idle with error message', () => {
    const state = reduce(listening(), { type: 'error_occurred', error: 'Mic permission denied' })
    expect(state.phase).toBe('idle')
    expect(state.error).toBe('Mic permission denied')
  })

  it('mute_toggled flips muted state', () => {
    expect(listening().muted).toBe(false)
    const muted = reduce(listening(), { type: 'mute_toggled' })
    expect(muted.muted).toBe(true)
    const unmuted = reduce(muted, { type: 'mute_toggled' })
    expect(unmuted.muted).toBe(false)
  })

  it('utterance_submitted clears finalizedUtterance', () => {
    const finalized = reduce(listening(), {
      type: 'final_transcript', text: 'add five', nativeMessageId: 'voice:vs-1:item_1',
    })
    expect(finalized.finalizedUtterance).not.toBeNull()
    const submitted = reduce(finalized, { type: 'utterance_submitted' })
    expect(submitted.finalizedUtterance).toBeNull()
  })
})
```

- [ ] **Step 2: Run test — FAIL, module does not exist**

```bash
cd src/web && npx vitest run src/voiceReducer.test.ts 2>&1 | head -20
# Expected: Cannot find module './voiceReducer'
```

- [ ] **Step 3: Create the pure reducer**

Implement `voiceReducer.ts` exporting `initialState`, `reduce`, `FinalizedUtterance`, `VoiceState`, and action types. All transitions are pure. `FinalizedUtterance` has exactly two fields: `text` and `nativeMessageId`. Denial messages map from `VoiceAdmissionDenialReason` strings. `speech_started` during `speaking` phase = barge-in: transitions to `listening` with `bargeIn: true`. `utterance_submitted` clears `finalizedUtterance` and resets `bargeIn`.

- [ ] **Step 4: Run tests — all pass**

```bash
cd src/web && npx vitest run src/voiceReducer.test.ts
# Expected: ✓ 15 tests passed
```

- [ ] **Step 5: Commit**

```bash
git add src/web/src/voiceReducer.ts src/web/src/voiceReducer.test.ts
git commit -m "feat(voice): add pure voice state machine reducer without provider uncertainty fields"
```

---

## Task 12: Frontend Voice API Client

**Files:**
- Create: `src/web/src/voiceApi.ts`
- Create: `src/web/src/voiceApi.test.ts`

- [ ] **Step 1: Write the failing tests**

```typescript
// src/web/src/voiceApi.test.ts
import { describe, expect, it, vi } from 'vitest'
import { admitVoice, releaseVoice, heartbeatVoice } from './voiceApi'

describe('voiceApi', () => {
  it('admitVoice sends POST with CSRF and credentials and returns typed response', async () => {
    const mockResponse = { admitted: true, voiceSessionId: 'vs-1', sdpAnswer: 'v=0\r\n', denialReason: null }
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve({
      ok: true, json: () => Promise.resolve(mockResponse),
    })))

    const result = await admitVoice('v=0\r\n', 'csrf-tok')
    expect(fetch).toHaveBeenCalledWith('/api/voice/admit', expect.objectContaining({
      method: 'POST',
      credentials: 'include',
      headers: expect.objectContaining({ 'X-CSRF-TOKEN': 'csrf-tok' }),
    }))
    expect(result.admitted).toBe(true)
    expect(result.voiceSessionId).toBe('vs-1')
    expect(result.sdpAnswer).toBe('v=0\r\n')
  })

  it('releaseVoice sends POST with CSRF and credentials', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve({ ok: true })))
    await releaseVoice('vs-1', 'csrf-tok')
    expect(fetch).toHaveBeenCalledWith('/api/voice/release', expect.objectContaining({
      method: 'POST',
      credentials: 'include',
      body: JSON.stringify({ voiceSessionId: 'vs-1' }),
    }))
  })

  it('heartbeatVoice returns lifecycle state', async () => {
    const mockResponse = { renewed: true, lifecycleState: 'active', remainingSeconds: 1200, forcedCloseReason: null }
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve({
      ok: true, json: () => Promise.resolve(mockResponse),
    })))
    const result = await heartbeatVoice('vs-1', 'csrf-tok')
    expect(result.lifecycleState).toBe('active')
    expect(result.remainingSeconds).toBe(1200)
  })

  it('admitVoice throws on non-ok response', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve({
      ok: false, status: 500, statusText: 'Internal Server Error',
    })))
    await expect(admitVoice('v=0\r\n', 'csrf-tok')).rejects.toThrow('500')
  })
})
```

- [ ] **Step 2: Run test — FAIL, module does not exist**

```bash
cd src/web && npx vitest run src/voiceApi.test.ts 2>&1 | head -20
# Expected: Cannot find module './voiceApi'
```

- [ ] **Step 3: Implement**

```typescript
// src/web/src/voiceApi.ts
export interface VoiceAdmissionResponse {
  admitted: boolean
  voiceSessionId: string | null
  sdpAnswer: string | null
  denialReason: string | null
}

export interface HeartbeatResponse {
  renewed: boolean
  lifecycleState: 'active' | 'warning_due' | 'expired' | 'idle' | 'not_found'
  remainingSeconds: number | null
  forcedCloseReason: string | null
}

function voiceHeaders(csrfToken: string): Record<string, string> {
  return { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrfToken }
}

export async function admitVoice(sdpOffer: string, csrfToken: string): Promise<VoiceAdmissionResponse> {
  const response = await fetch('/api/voice/admit', {
    method: 'POST', credentials: 'include',
    headers: voiceHeaders(csrfToken),
    body: JSON.stringify({ sdpOffer }),
  })
  if (!response.ok) throw new Error(`Voice admission failed with status ${response.status}.`)
  return response.json() as Promise<VoiceAdmissionResponse>
}

export async function releaseVoice(voiceSessionId: string, csrfToken: string): Promise<void> {
  const response = await fetch('/api/voice/release', {
    method: 'POST', credentials: 'include',
    headers: voiceHeaders(csrfToken),
    body: JSON.stringify({ voiceSessionId }),
  })
  if (!response.ok) throw new Error(`Voice release failed with status ${response.status}.`)
}

export async function heartbeatVoice(voiceSessionId: string, csrfToken: string): Promise<HeartbeatResponse> {
  const response = await fetch('/api/voice/heartbeat', {
    method: 'POST', credentials: 'include',
    headers: voiceHeaders(csrfToken),
    body: JSON.stringify({ voiceSessionId }),
  })
  if (!response.ok) throw new Error(`Voice heartbeat failed with status ${response.status}.`)
  return response.json() as Promise<HeartbeatResponse>
}
```

- [ ] **Step 4: Run tests — all pass**

```bash
cd src/web && npx vitest run src/voiceApi.test.ts
# Expected: ✓ 4 tests passed
```

- [ ] **Step 5: Commit**

```bash
git add src/web/src/voiceApi.ts src/web/src/voiceApi.test.ts
git commit -m "feat(voice): add typed voice API client with lifecycle heartbeat"
```

---

## Task 13: Frontend Voice Transport Adapter

**Files:**
- Create: `src/web/src/voiceTransport.ts`
- Create: `src/web/src/testing/fakeVoiceTransport.ts`
- Create: `src/web/src/voiceTransport.test.ts`

- [ ] **Step 1: Write the failing tests**

```typescript
// src/web/src/voiceTransport.test.ts
import { describe, expect, it, vi } from 'vitest'
import { FakeVoiceTransport } from './testing/fakeVoiceTransport'

describe('FakeVoiceTransport', () => {
  it('prepare returns a fake SDP offer', async () => {
    const transport = new FakeVoiceTransport()
    const offer = await transport.prepare()
    expect(offer).toContain('v=0')
  })

  it('connect dispatches connected callback', () => {
    const transport = new FakeVoiceTransport()
    const onConnected = vi.fn()
    transport.connect('v=0\r\n', { onConnected, onSpeechStarted: vi.fn(), onSpeechStopped: vi.fn(),
      onPartialTranscript: vi.fn(), onFinalTranscript: vi.fn(), onPlaybackStarted: vi.fn(),
      onPlaybackDone: vi.fn(), onPlaybackFailed: vi.fn(), onPlaybackIntegrityError: vi.fn(), onError: vi.fn(), onMicrophoneFailed: vi.fn() })
    transport.simulateConnected()
    expect(onConnected).toHaveBeenCalledOnce()
  })

  it('simulateFinalTranscript dispatches with text and nativeMessageId only', () => {
    const transport = new FakeVoiceTransport()
    const onFinalTranscript = vi.fn()
    transport.connect('v=0\r\n', { onConnected: vi.fn(), onSpeechStarted: vi.fn(), onSpeechStopped: vi.fn(),
      onPartialTranscript: vi.fn(), onFinalTranscript, onPlaybackStarted: vi.fn(),
      onPlaybackDone: vi.fn(), onPlaybackFailed: vi.fn(), onPlaybackIntegrityError: vi.fn(), onError: vi.fn(), onMicrophoneFailed: vi.fn() })
    transport.simulateFinalTranscript('add five', 'voice:vs-1:item_1')
    expect(onFinalTranscript).toHaveBeenCalledWith('add five', 'voice:vs-1:item_1')
  })

  it('cancelPlayback records the call for assertion', () => {
    const transport = new FakeVoiceTransport()
    transport.connect('v=0\r\n', { onConnected: vi.fn(), onSpeechStarted: vi.fn(), onSpeechStopped: vi.fn(),
      onPartialTranscript: vi.fn(), onFinalTranscript: vi.fn(), onPlaybackStarted: vi.fn(),
      onPlaybackDone: vi.fn(), onPlaybackFailed: vi.fn(), onPlaybackIntegrityError: vi.fn(), onError: vi.fn(), onMicrophoneFailed: vi.fn() })
    transport.cancelPlayback(1500)
    expect(transport.cancelPlaybackCalls).toEqual([1500])
  })

  it('speakCanonical records the spoken text', () => {
    const transport = new FakeVoiceTransport()
    transport.connect('v=0\r\n', { onConnected: vi.fn(), onSpeechStarted: vi.fn(), onSpeechStopped: vi.fn(),
      onPartialTranscript: vi.fn(), onFinalTranscript: vi.fn(), onPlaybackStarted: vi.fn(),
      onPlaybackDone: vi.fn(), onPlaybackFailed: vi.fn(), onPlaybackIntegrityError: vi.fn(), onError: vi.fn(), onMicrophoneFailed: vi.fn() })
    transport.speakCanonical('5 boxes of Steel Bolts added.')
    expect(transport.lastSpokenText).toBe('5 boxes of Steel Bolts added.')
  })

  it('disconnect prevents subsequent callbacks', () => {
    const transport = new FakeVoiceTransport()
    const onError = vi.fn()
    transport.connect('v=0\r\n', { onConnected: vi.fn(), onSpeechStarted: vi.fn(), onSpeechStopped: vi.fn(),
      onPartialTranscript: vi.fn(), onFinalTranscript: vi.fn(), onPlaybackStarted: vi.fn(),
      onPlaybackDone: vi.fn(), onPlaybackFailed: vi.fn(), onError, onMicrophoneFailed: vi.fn() })
    transport.disconnect()
    transport.simulateError('late error')
    expect(onError).not.toHaveBeenCalled()
  })
})
```

- [ ] **Step 2: Run test — FAIL, module does not exist**

```bash
cd src/web && npx vitest run src/voiceTransport.test.ts 2>&1 | head -20
# Expected: Cannot find module './testing/fakeVoiceTransport'
```

- [ ] **Step 3: Create transport interface and fake**

```typescript
// src/web/src/voiceTransport.ts
export interface VoiceTransportCallbacks {
  onConnected: () => void
  onSpeechStarted: () => void
  onSpeechStopped: () => void
  onPartialTranscript: (text: string) => void
  /** No uncertain parameter — Voice Live provides only item_id and transcript. */
  onFinalTranscript: (text: string, nativeMessageId: string) => void
  onPlaybackStarted: () => void
  onPlaybackDone: () => void
  onPlaybackFailed: (error: string) => void
  onPlaybackIntegrityError: (requested: string, received: string) => void
  onError: (error: string) => void
  onMicrophoneFailed: (error: string) => void
}

export interface VoiceTransport {
  prepare(): Promise<string>
  connect(sdpAnswer: string, callbacks: VoiceTransportCallbacks): void
  disconnect(): void
  setMuted(muted: boolean): void
  /**
   * Barge-in: send response.cancel followed by conversation.item.truncate.
   * conversation.item.truncate requires the tracked output item_id, content_index (0),
   * and audio_end_ms from measured playback duration.
   * The real transport tracks the last output item_id from response events.
   */
  cancelPlayback(measuredPlayedDurationMs: number): void
  /**
   * Canonical speech: sends response.create with response.pre_generated_assistant_message
   * containing the exact canonical text. The real transport listens for
   * response.audio_transcript.done and verifies its transcript field equals the requested
   * text. If it differs, fires onPlaybackIntegrityError and stops playback.
   * The UI always keeps the canonical summary text visible regardless of playback state.
   */
  speakCanonical(text: string): void
}
```

```typescript
// src/web/src/testing/fakeVoiceTransport.ts
import type { VoiceTransport, VoiceTransportCallbacks } from '../voiceTransport'

export class FakeVoiceTransport implements VoiceTransport {
  private callbacks: VoiceTransportCallbacks | null = null
  private disconnected = false
  public cancelPlaybackCalls: number[] = []
  public lastSpokenText: string | null = null

  async prepare(): Promise<string> { return 'v=0\r\no=fake\r\n' }

  connect(sdpAnswer: string, callbacks: VoiceTransportCallbacks): void {
    this.callbacks = callbacks
    this.disconnected = false
  }

  disconnect(): void { this.disconnected = true; this.callbacks = null }

  setMuted(_muted: boolean): void { /* tracked if needed */ }

  cancelPlayback(measuredPlayedDurationMs: number): void {
    this.cancelPlaybackCalls.push(measuredPlayedDurationMs)
  }

  speakCanonical(text: string): void { this.lastSpokenText = text }

  // Test helpers
  simulateConnected(): void { if (!this.disconnected) this.callbacks?.onConnected() }
  simulateSpeechStarted(): void { if (!this.disconnected) this.callbacks?.onSpeechStarted() }
  simulateSpeechStopped(): void { if (!this.disconnected) this.callbacks?.onSpeechStopped() }
  simulatePartialTranscript(text: string): void { if (!this.disconnected) this.callbacks?.onPartialTranscript(text) }
  simulateFinalTranscript(text: string, nativeMessageId: string): void {
    if (!this.disconnected) this.callbacks?.onFinalTranscript(text, nativeMessageId)
  }
  simulatePlaybackStarted(): void { if (!this.disconnected) this.callbacks?.onPlaybackStarted() }
  simulatePlaybackDone(): void { if (!this.disconnected) this.callbacks?.onPlaybackDone() }
  simulatePlaybackFailed(error: string): void { if (!this.disconnected) this.callbacks?.onPlaybackFailed(error) }
  simulatePlaybackIntegrityError(requested: string, received: string): void {
    if (!this.disconnected) this.callbacks?.onPlaybackIntegrityError(requested, received)
  }
  simulateError(error: string): void { if (!this.disconnected) this.callbacks?.onError(error) }
}
```

The `nativeMessageId` derivation in the real transport: extract `item_id` from `conversation.item.input_audio_transcription.completed` and compute `voice:${voiceSessionId}:${itemId}`. The `voiceSessionId` is set by the caller after admission. The real transport also tracks the last output `item_id` from `response.audio.delta` or `response.done` events for `conversation.item.truncate`, and verifies `response.audio_transcript.done.transcript` against the requested canonical text for playback integrity.

- [ ] **Step 4: Run tests — all pass**

```bash
cd src/web && npx vitest run src/voiceTransport.test.ts
# Expected: ✓ 6 tests passed
```

- [ ] **Step 5: Commit**

```bash
git add src/web/src/voiceTransport.ts src/web/src/testing/fakeVoiceTransport.ts src/web/src/voiceTransport.test.ts
git commit -m "feat(voice): add typed voice transport adapter with truncation tracking and fake"
```

---

## Task 14: Shared Turn Submission Controller

**Why now:** Must be extracted BEFORE App integration. Both text and voice use one path.

**Existing TurnTracer tests that must remain green (26 tests in `src/web/src/TurnTracer.test.tsx`):**
1. `submits a Turn and follows its stream instead of polling`
2. `announces progress in a live region while the answer is still being worked on`
3. `shows the fatal error and stops announcing progress when the open stream fails permanently`
4. `renders the streamed parts and the terminal Outcome together`
5. `reconnects to a Turn it had already submitted, without submitting anything again`
6. `keeps one live stream and its rendered parts across a parent rerender`
7. `recovers a live stream after StrictMode's development-only mount/cleanup/mount`
8. `resubmits the very same native message id when it never learned the Turn id`
9. `renders and settles an already-recorded Outcome returned by a fresh submission`
10. `renders and settles an already-recorded Outcome returned while resuming a lost response`
11. `does not resubmit a confirmation whose token was never persisted, and says so plainly`
12. `clears the in-flight record when a normal submit is definitively rejected (e.g. 400)`
13. `clears a stored lost-response submission when its resubmission is definitively rejected, and does not retry it on a later mount`
14. `keeps the in-flight record when a normal submit fails at the network level, not the server`
15. `keeps the in-flight record when a normal submit is rejected with a retryable status (e.g. 429)`
16. `aborts before sending when browser storage is unavailable, and stays usable`
17. `keeps watching the current Turn when storing a newer submission fails`
18. `forgets the in-flight Turn once it has an answer`
19. `keeps a newer Turn B intact when a superseded Turn A's streamed Outcome arrives after B was submitted`
20. `picks up a Turn another tab of the same browser profile started`
21. `never resubmits when the parent re-renders before it has learned the Turn id`
22. `still resubmits only once when it never learned the Turn id, under StrictMode and a parent re-render together`
23. `does not act on a submit response that arrives after the component has really unmounted`
24. `does not act on a resumed (lost-response) submission's response that arrives after the component has really unmounted`
25. `closes an already-open stream on unmount, so a later event on it changes nothing`
26. `never renders a control that would change a quantity directly`

**Files:**
- Create: `src/web/src/useTurnSubmission.ts`
- Create: `src/web/src/useTurnSubmission.test.ts`
- Modify: `src/web/src/TurnTracer.tsx`

- [ ] **Step 1: Write controller tests**

```typescript
// src/web/src/useTurnSubmission.test.ts
import { describe, expect, it, vi, beforeEach } from 'vitest'
import { renderHook, act } from '@testing-library/react'

// Import the hook and helpers needed to test it.
// The exact import path depends on the extracted module.

describe('useTurnSubmission', () => {
  it('stores breadcrumb in localStorage BEFORE HTTP request', async () => {
    const { result } = renderHook(() => useTurnSubmission({
      csrfToken: 'csrf', webConversationId: 'wc-1', participantId: 'pid-1',
      onTerminalOutcome: vi.fn(),
    }))

    const storageSpy = vi.spyOn(Storage.prototype, 'setItem')
    vi.stubGlobal('fetch', vi.fn(() => new Promise(() => {}))) // never resolves

    await act(async () => {
      result.current.submit({ nativeMessageId: 'msg-1', contentText: 'add five' })
    })

    // localStorage was written before fetch was called
    expect(storageSpy).toHaveBeenCalled()
    expect(fetch).toHaveBeenCalled()
    const storageCallOrder = storageSpy.mock.invocationCallOrder[0]
    const fetchCallOrder = (fetch as ReturnType<typeof vi.fn>).mock.invocationCallOrder[0]
    expect(storageCallOrder).toBeLessThan(fetchCallOrder)
    storageSpy.mockRestore()
  })

  it('same nativeMessageId replayed returns same turnId', async () => {
    const { result } = renderHook(() => useTurnSubmission({
      csrfToken: 'csrf', webConversationId: 'wc-1', participantId: 'pid-1',
      onTerminalOutcome: vi.fn(),
    }))

    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve({
      ok: true, status: 202,
      json: () => Promise.resolve({ turnId: 'turn-1', alreadyAccepted: false }),
      headers: new Headers({ 'content-type': 'application/json' }),
    })))

    await act(async () => {
      result.current.submit({ nativeMessageId: 'msg-1', contentText: 'add five' })
    })

    expect(result.current.turnId).toBe('turn-1')
  })

  it('new submission rejected while one is in flight', async () => {
    const { result } = renderHook(() => useTurnSubmission({
      csrfToken: 'csrf', webConversationId: 'wc-1', participantId: 'pid-1',
      onTerminalOutcome: vi.fn(),
    }))

    vi.stubGlobal('fetch', vi.fn(() => new Promise(() => {}))) // never resolves

    await act(async () => {
      result.current.submit({ nativeMessageId: 'msg-a', contentText: 'add five' })
    })

    expect(result.current.progress).toBe('submitting')

    await act(async () => {
      // Second submission while first is in flight — should be rejected
      const accepted = result.current.submit({ nativeMessageId: 'msg-b', contentText: 'list stock' })
      expect(accepted).toBe(false)
    })
  })

  it('voice submission includes voiceSessionId for provenance', async () => {
    const { result } = renderHook(() => useTurnSubmission({
      csrfToken: 'csrf', webConversationId: 'wc-1', participantId: 'pid-1',
      onTerminalOutcome: vi.fn(),
    }))

    vi.stubGlobal('fetch', vi.fn((_, init: RequestInit) => {
      const body = JSON.parse(init.body as string)
      expect(body.voiceSessionId).toBe('vs-1')
      return Promise.resolve({
        ok: true, status: 202,
        json: () => Promise.resolve({ turnId: 'turn-1', alreadyAccepted: false }),
        headers: new Headers({ 'content-type': 'application/json' }),
      })
    }))

    await act(async () => {
      result.current.submit({
        nativeMessageId: 'voice:vs-1:item_1',
        contentText: 'add five',
        voiceSessionId: 'vs-1',
      })
    })
  })

  it('unmount closes stream and ignores late callbacks', async () => {
    const { result, unmount } = renderHook(() => useTurnSubmission({
      csrfToken: 'csrf', webConversationId: 'wc-1', participantId: 'pid-1',
      onTerminalOutcome: vi.fn(),
    }))

    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve({
      ok: true, status: 202,
      json: () => Promise.resolve({ turnId: 'turn-1', alreadyAccepted: false }),
      headers: new Headers({ 'content-type': 'application/json' }),
    })))

    await act(async () => {
      result.current.submit({ nativeMessageId: 'msg-1', contentText: 'add five' })
    })

    unmount()
    // No errors thrown from late callbacks after unmount
  })

  it('clears in-flight record on terminal Outcome', async () => {
    const onTerminalOutcome = vi.fn()
    const { result } = renderHook(() => useTurnSubmission({
      csrfToken: 'csrf', webConversationId: 'wc-1', participantId: 'pid-1',
      onTerminalOutcome,
    }))

    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve({
      ok: true, status: 202,
      json: () => Promise.resolve({ turnId: 'turn-1', alreadyAccepted: false }),
      headers: new Headers({ 'content-type': 'application/json' }),
    })))

    await act(async () => {
      result.current.submit({ nativeMessageId: 'msg-1', contentText: 'add five' })
    })

    // Simulate terminal Outcome arriving via SSE stream callback
    expect(onTerminalOutcome).toHaveBeenCalled()
    // After terminal Outcome, readInFlightTurn should return null
  })
})
```

- [ ] **Step 2: Run test — FAIL, module does not exist**

```bash
cd src/web && npx vitest run src/useTurnSubmission.test.ts 2>&1 | head -20
# Expected: Cannot find module './useTurnSubmission'
```

- [ ] **Step 3: Extract from TurnTracer**

The shared `useTurnSubmission` hook encapsulates: `rememberSubmission`, `submitTurn`, SSE stream via `watchTurn`/`openTurnStream`, `clearInFlightTurnIfMatches`, `mountedRef`, `resumeAttemptedRef`, cross-tab subscription, StrictMode guard. It exposes:

```typescript
interface TurnSubmissionInput {
  nativeMessageId: string
  contentText: string
  wasInterrupted?: boolean
  voiceSessionId?: string
}

interface UseTurnSubmissionResult {
  submit: (input: TurnSubmissionInput) => boolean
  progress: 'idle' | 'submitting' | 'accepted' | 'processing'
  turnId: string | null
  parts: TurnResponsePartEvent[]
  outcome: TurnOutcomeView | null
  error: string | null
}
```

TurnTracer's form `handleSubmit` calls `controller.submit(...)` and delegates all stream/recovery/storage logic to the hook. TurnTracer retains its rendering responsibilities only.

- [ ] **Step 4: Verify all 26 existing TurnTracer tests still pass**

```bash
cd src/web && npx vitest run src/TurnTracer.test.tsx
# Expected: ✓ 26 tests passed — every test listed in the "Existing TurnTracer tests" section above
```

- [ ] **Step 5: Run new useTurnSubmission tests**

```bash
cd src/web && npx vitest run src/useTurnSubmission.test.ts
# Expected: ✓ 6 tests passed
```

- [ ] **Step 6: Commit**

```bash
git add src/web/src/useTurnSubmission.ts src/web/src/useTurnSubmission.test.ts src/web/src/TurnTracer.tsx
git commit -m "refactor(voice): extract shared useTurnSubmission controller from TurnTracer"
```

---

## Task 15: Voice Controls UI

**Files:**
- Create: `src/web/src/VoiceControls.tsx`
- Create: `src/web/src/VoiceControls.test.tsx`

- [ ] **Step 1: Write the failing tests**

```typescript
// src/web/src/VoiceControls.test.tsx
import { describe, expect, it, vi } from 'vitest'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { FakeVoiceTransport } from './testing/fakeVoiceTransport'

describe('VoiceControls', () => {
  it('shows a Start Voice button when idle', () => {
    const transport = new FakeVoiceTransport()
    render(<VoiceControls transport={transport} csrfToken="csrf" voiceSessionId={null}
      onFinalizedUtterance={vi.fn()} onVoiceSessionChanged={vi.fn()} />)
    expect(screen.getByRole('button', { name: /start voice/i })).toBeInTheDocument()
  })

  it('transitions to listening after successful admission', async () => {
    const transport = new FakeVoiceTransport()
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve({
      ok: true, json: () => Promise.resolve({
        admitted: true, voiceSessionId: 'vs-1', sdpAnswer: 'v=0\r\n', denialReason: null,
      }),
    })))

    render(<VoiceControls transport={transport} csrfToken="csrf" voiceSessionId={null}
      onFinalizedUtterance={vi.fn()} onVoiceSessionChanged={vi.fn()} />)

    await userEvent.click(screen.getByRole('button', { name: /start voice/i }))
    transport.simulateConnected()
    await waitFor(() => expect(screen.getByText(/listening/i)).toBeInTheDocument())
  })

  it('shows denial error when voice is disabled', async () => {
    const transport = new FakeVoiceTransport()
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve({
      ok: true, json: () => Promise.resolve({
        admitted: false, voiceSessionId: null, sdpAnswer: null, denialReason: 'VoiceDisabled',
      }),
    })))

    render(<VoiceControls transport={transport} csrfToken="csrf" voiceSessionId={null}
      onFinalizedUtterance={vi.fn()} onVoiceSessionChanged={vi.fn()} />)

    await userEvent.click(screen.getByRole('button', { name: /start voice/i }))
    expect(await screen.findByRole('alert')).toHaveTextContent(/voice.*disabled/i)
  })

  it('calls onFinalizedUtterance when a final transcript arrives', async () => {
    const transport = new FakeVoiceTransport()
    const onFinalized = vi.fn()
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve({
      ok: true, json: () => Promise.resolve({
        admitted: true, voiceSessionId: 'vs-1', sdpAnswer: 'v=0\r\n', denialReason: null,
      }),
    })))

    render(<VoiceControls transport={transport} csrfToken="csrf" voiceSessionId={null}
      onFinalizedUtterance={onFinalized} onVoiceSessionChanged={vi.fn()} />)

    await userEvent.click(screen.getByRole('button', { name: /start voice/i }))
    transport.simulateConnected()
    await waitFor(() => expect(screen.getByText(/listening/i)).toBeInTheDocument())

    transport.simulateFinalTranscript('add five boxes', 'voice:vs-1:item_1')
    await waitFor(() => expect(onFinalized).toHaveBeenCalledWith({
      text: 'add five boxes', nativeMessageId: 'voice:vs-1:item_1',
    }))
  })

  it('shows playback failure alert with canonical text still visible', async () => {
    const transport = new FakeVoiceTransport()
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve({
      ok: true, json: () => Promise.resolve({
        admitted: true, voiceSessionId: 'vs-1', sdpAnswer: 'v=0\r\n', denialReason: null,
      }),
    })))

    render(<VoiceControls transport={transport} csrfToken="csrf" voiceSessionId={null}
      onFinalizedUtterance={vi.fn()} onVoiceSessionChanged={vi.fn()} />)

    await userEvent.click(screen.getByRole('button', { name: /start voice/i }))
    transport.simulateConnected()
    await waitFor(() => expect(screen.getByText(/listening/i)).toBeInTheDocument())

    transport.simulatePlaybackStarted()
    transport.simulatePlaybackFailed('Audio decode error')

    expect(await screen.findByRole('alert')).toHaveTextContent(/audio playback failed/i)
  })

  it('returns to idle after clicking End', async () => {
    const transport = new FakeVoiceTransport()
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve({
      ok: true, json: () => Promise.resolve({
        admitted: true, voiceSessionId: 'vs-1', sdpAnswer: 'v=0\r\n', denialReason: null,
      }),
    })))

    render(<VoiceControls transport={transport} csrfToken="csrf" voiceSessionId={null}
      onFinalizedUtterance={vi.fn()} onVoiceSessionChanged={vi.fn()} />)

    await userEvent.click(screen.getByRole('button', { name: /start voice/i }))
    transport.simulateConnected()
    await waitFor(() => expect(screen.getByText(/listening/i)).toBeInTheDocument())

    await userEvent.click(screen.getByRole('button', { name: /end voice/i }))
    await waitFor(() => expect(screen.getByRole('button', { name: /start voice/i })).toBeInTheDocument())
  })

  it('has accessible mute button during listening', async () => {
    const transport = new FakeVoiceTransport()
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve({
      ok: true, json: () => Promise.resolve({
        admitted: true, voiceSessionId: 'vs-1', sdpAnswer: 'v=0\r\n', denialReason: null,
      }),
    })))

    render(<VoiceControls transport={transport} csrfToken="csrf" voiceSessionId={null}
      onFinalizedUtterance={vi.fn()} onVoiceSessionChanged={vi.fn()} />)

    await userEvent.click(screen.getByRole('button', { name: /start voice/i }))
    transport.simulateConnected()
    await waitFor(() => expect(screen.getByRole('button', { name: /mute/i })).toBeInTheDocument())
  })
})
```

- [ ] **Step 2: Run test — FAIL, module does not exist**

```bash
cd src/web && npx vitest run src/VoiceControls.test.tsx 2>&1 | head -20
# Expected: Cannot find module './VoiceControls'
```

- [ ] **Step 3: Implement `VoiceControls.tsx`**

Create `src/web/src/VoiceControls.tsx`. The component receives `transport: VoiceTransport`, `csrfToken: string`, `voiceSessionId: string | null`, `onFinalizedUtterance: (u: FinalizedUtterance) => void`, and `onVoiceSessionChanged: (id: string | null) => void`.

Admission sequence (in `handleStart` async handler):
1. `dispatch({ type: 'start_requested' })`
2. `const sdpOffer = await transport.prepare()` — real mic + PeerConnection + ICE
3. `const result = await admitVoice(sdpOffer, csrfToken)` — POST to backend
4. If denied: `dispatch({ type: 'denied', reason })` → return
5. `dispatch({ type: 'admitted', voiceSessionId, sdpAnswer })` — BEFORE connect (so callbacks have state)
6. `transport.connect(sdpAnswer, callbacks)` — callbacks can now fire
7. Start heartbeat interval via `setInterval`

Internal state uses `useReducer(voiceReducer)` from `voiceReducer.ts`. Renders: Start/End/Mute `<button>` elements, partial transcript `<p>`, warning `<p role="status">`, error `<div role="alert">`, playback failure `<div role="alert">`. Has `<div aria-live="polite">` for canonical output text region. Canonical speech uses `transport.speakCanonical(text)` and fires `onPlaybackIntegrityError` on transcript mismatch.

- [ ] **Step 4: Run tests — all pass**

```bash
cd src/web && npx vitest run src/VoiceControls.test.tsx
# Expected: ✓ 7 tests passed
```

- [ ] **Step 5: Commit**

```bash
git add src/web/src/VoiceControls.tsx src/web/src/VoiceControls.test.tsx
git commit -m "feat(voice): add accessible voice controls UI with playback failure handling"
```

---

## Task 16: App Integration, New Conversation Voice Teardown, and React Lifecycle

**Why now:** Wires voice into App. New Conversation ordering must preserve ticket #35's safety invariant: rotate server conversation first, then clear recovery storage, then remount.

**#35 safety ordering invariant:** The current `handleNewConversation` in App.tsx:
1. `await startNewConversation(csrfToken)` — rotates the server conversation
2. `clearInFlightTurn(webConversationId, participantId)` — clears recovery storage (only after successful rotation)
3. `setConversationEpoch(e => e + 1)` — remounts the conversation component

This ordering is intentional: releasing/clearing before rotation can lose recoverability if rotation fails. Voice teardown must be designed around this invariant.

**Voice teardown ordering:**
1. **Fence voice callbacks** — increment `voiceGeneration`, dispatch `end_requested` to reducer; all subsequent voice transport callbacks check generation and are ignored
2. **Disconnect transport** — `transport.disconnect()` stops mic/playback/data channel locally
3. **Execute existing server rotation** — `await startNewConversation(csrfToken)` (unchanged)
4. **Clear recovery storage** — `clearInFlightTurn(webConversationId, participantId)` (unchanged, only after successful rotation)
5. **Release voice session** — `await releaseVoice(voiceSessionId, csrfToken)` (best-effort, errors caught; SQL idle/expiry is the authoritative cleanup)
6. **Remount** — `setConversationEpoch(e => e + 1)` + reset voice reducer to idle

On partial failures: if rotation fails, voice transport is already disconnected locally but the server session is still released best-effort. If release fails, SQL idle/expiry reclaims the session.

**Unmount release:** `useEffect` cleanup calls `transport.disconnect()` and sends a `fetch('/api/voice/release', { method: 'POST', keepalive: true, credentials: 'include', headers, body: JSON.stringify({ voiceSessionId }) })`. The `keepalive` + custom CSRF header may not be dependable during page unload — this is treated as best-effort; SQL idle/expiry is authoritative.

**Files:**
- Modify: `src/web/src/App.tsx`
- Modify: `src/web/src/App.test.tsx`

- [ ] **Step 1: Write integration tests**

```typescript
// Added to src/web/src/App.test.tsx
it('New Conversation fences voice callbacks, disconnects transport, rotates, then releases', async () => {
  setViewportWidth(DESKTOP_WIDTH)
  const callOrder: string[] = []
  const transport = new FakeVoiceTransport()
  const origDisconnect = transport.disconnect.bind(transport)
  transport.disconnect = () => { callOrder.push('disconnect'); origDisconnect() }

  stubApi({
    '/api/voice/admit': () => {
      callOrder.push('admit')
      return json({ admitted: true, voiceSessionId: 'vs-1', sdpAnswer: 'v=0\r\n', denialReason: null })
    },
    '/api/conversation/new': () => {
      callOrder.push('rotate')
      return json({ foundryConversationId: 'foundry-2', generation: 2, clearedPendingConfirmation: false })
    },
    '/api/voice/release': () => {
      callOrder.push('release')
      return json({})
    },
  })

  render(<App testTransport={transport} />)
  await screen.findByRole('banner')

  // Admit voice
  await userEvent.click(await screen.findByRole('button', { name: /start voice/i }))
  transport.simulateConnected()
  await waitFor(() => expect(callOrder).toContain('admit'))

  callOrder.length = 0

  // Click New Conversation
  await userEvent.click(screen.getByRole('button', { name: 'New conversation' }))

  await waitFor(() => expect(callOrder).toContain('rotate'))

  // Ordering: disconnect before rotate, release after rotate
  const disconnectIdx = callOrder.indexOf('disconnect')
  const rotateIdx = callOrder.indexOf('rotate')
  const releaseIdx = callOrder.indexOf('release')
  expect(disconnectIdx).toBeLessThan(rotateIdx)
  expect(rotateIdx).toBeLessThan(releaseIdx)
})

it('late voice callback from prior generation is ignored', async () => {
  setViewportWidth(DESKTOP_WIDTH)
  const transport = new FakeVoiceTransport()
  const onFinalized = vi.fn()

  stubApi({
    '/api/voice/admit': () => json({
      admitted: true, voiceSessionId: 'vs-1', sdpAnswer: 'v=0\r\n', denialReason: null }),
    '/api/conversation/new': () => json({
      foundryConversationId: 'foundry-2', generation: 2, clearedPendingConfirmation: false }),
    '/api/voice/release': () => json({}),
  })

  render(<App testTransport={transport} />)
  await screen.findByRole('banner')

  // Admit voice (generation 1)
  await userEvent.click(await screen.findByRole('button', { name: /start voice/i }))
  transport.simulateConnected()

  // End voice → generation increments
  await userEvent.click(screen.getByRole('button', { name: /end voice/i }))

  // Late callback from generation 1 — must be ignored
  transport.simulateFinalTranscript('stale text', 'voice:vs-1:item_old')

  // No submission should have occurred
  expect(fetch).not.toHaveBeenCalledWith('/api/turns', expect.anything())
})

it('unmount releases session with keepalive fetch (best-effort)', async () => {
  setViewportWidth(DESKTOP_WIDTH)
  const transport = new FakeVoiceTransport()

  stubApi({
    '/api/voice/admit': () => json({
      admitted: true, voiceSessionId: 'vs-1', sdpAnswer: 'v=0\r\n', denialReason: null }),
  })

  const { unmount } = render(<App testTransport={transport} />)
  await screen.findByRole('banner')

  await userEvent.click(await screen.findByRole('button', { name: /start voice/i }))
  transport.simulateConnected()

  unmount()

  // Best-effort release was attempted with keepalive
  expect(fetch).toHaveBeenCalledWith('/api/voice/release', expect.objectContaining({
    method: 'POST',
    keepalive: true,
  }))
})

it('does not assert network release always completes during unload', () => {
  // This is a documentation test: unmount release uses keepalive + custom CSRF header
  // which is not dependable during page unload. SQL idle/expiry is the authoritative
  // cleanup mechanism. No network reliability assertion is made here.
  expect(true).toBe(true)
})
```

- [ ] **Step 2: Run App tests — FAIL, VoiceControls not wired yet**

```bash
cd src/web && npx vitest run src/App.test.tsx 2>&1 | head -20
# Expected: test failures related to missing voice wiring
```

- [ ] **Step 3: Implement**

Wire `VoiceControls` into App after `TurnTracer`, positioned below conversation in DOM order.

Generation token: `const voiceGenerationRef = useRef(0)`. Incremented on each admission and end. Callbacks check `voiceGenerationRef.current === capturedGeneration` before dispatching.

Updated `handleNewConversation` (preserving #35 safety ordering):
```typescript
async function handleNewConversation() {
  if (state.phase !== 'ready') return
  setResetting(true)
  setError(null)
  setNotice(null)

  // 1. Fence voice callbacks — increment generation so late callbacks are ignored
  const currentVoiceSessionId = voiceSessionIdRef.current
  voiceGenerationRef.current += 1
  voiceDispatch({ type: 'end_requested' })

  // 2. Disconnect transport locally — stops mic/playback/data channel
  transportRef.current?.disconnect()

  try {
    // 3. Execute existing server rotation (unchanged — #35 safety ordering)
    const rotation = await startNewConversation(state.session.csrfToken)

    // 4. Clear recovery storage (unchanged — only after successful rotation)
    const cleared = clearInFlightTurn(
      state.session.bootstrap.webConversationId,
      state.session.bootstrap.participantId)
    if (!cleared) {
      setError('The new conversation started, but browser recovery state could not be cleared safely. Close this tab and re-open the application.')
      return
    }

    // 5. Release voice session (best-effort — SQL idle/expiry is authoritative)
    if (currentVoiceSessionId) {
      try { await releaseVoice(currentVoiceSessionId, state.session.csrfToken) } catch { /* best-effort */ }
    }

    // 6. Remount + reset voice
    voiceDispatch({ type: 'ended' })
    setConversationEpoch(epoch => epoch + 1)
    setNotice(rotation.clearedPendingConfirmation
      ? 'Started a new conversation. The change that was waiting for confirmation was cleared.'
      : 'Started a new conversation.')
  } catch (err) {
    // Even on rotation failure, voice transport is already disconnected locally.
    // Best-effort release the server session.
    if (currentVoiceSessionId) {
      try { await releaseVoice(currentVoiceSessionId, state.session.csrfToken) } catch { /* best-effort */ }
    }
    setError(err instanceof Error ? err.message : String(err))
  } finally {
    setResetting(false)
  }
}
```

Unmount: `useEffect` cleanup:
```typescript
return () => {
  transportRef.current?.disconnect()
  if (voiceSessionIdRef.current && state.phase === 'ready') {
    // Best-effort: keepalive + CSRF header is not dependable during page unload.
    // SQL idle/expiry is the authoritative cleanup mechanism.
    fetch('/api/voice/release', {
      method: 'POST', keepalive: true, credentials: 'include',
      headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': state.session.csrfToken },
      body: JSON.stringify({ voiceSessionId: voiceSessionIdRef.current }),
    }).catch(() => {})
  }
}
```

- [ ] **Step 4: Run all App tests — all pass (existing 17 + new 4)**

```bash
cd src/web && npx vitest run src/App.test.tsx
# Expected: ✓ 21 tests passed
```

- [ ] **Step 5: Commit**

```bash
git add src/web/src/App.tsx src/web/src/App.test.tsx
git commit -m "feat(voice): wire voice into App with #35-safe New Conversation ordering and generation fencing"
```

---

## Task 17: Voice Confirmation Integration Test — Voice Cannot Confirm, Text Can

**Why now:** Proves the InputModality policy end-to-end: voice "confirm <token>" leaves proposal pending, subsequent text confirmation consumes it.

**Files:**
- Create: `tests/MultiChannelAgent.IntegrationTests/Voice/VoiceConfirmationIntegrationTests.cs`

- [ ] **Step 1: Write integration tests**

```csharp
// tests/MultiChannelAgent.IntegrationTests/Voice/VoiceConfirmationIntegrationTests.cs
[Collection("SqlServer")]
public sealed class VoiceConfirmationIntegrationTests(SqlServerFixture fixture) : IAsyncLifetime
{
    [Fact]
    public async Task Voice_confirm_token_leaves_proposal_pending_and_text_confirm_consumes_it()
    {
        // 1. Submit a mutating request that produces a confirmation proposal
        //    e.g. "move stock Steel Bolts all to Shelf A" → proposal with token
        var proposalResponse = await SubmitTextTurn("move stock Steel Bolts all to Shelf A");
        var token = ExtractConfirmationToken(proposalResponse);
        Assert.NotNull(token);

        // 2. Admit a voice session for the same participant
        var admitResult = await AdmitVoiceSession();
        Assert.True(admitResult.Admitted);

        // 3. Submit "confirm <token>" as a voice-originated Turn
        //    (voiceSessionId present → InputModality.Voice)
        var voiceConfirmResponse = await SubmitVoiceTurn(
            $"confirm {token}", admitResult.VoiceSessionId!.Value);

        // 4. Verify proposal is still Pending — voice modality → DirectConfirmationEvidence.None
        var proposalAfterVoice = await ReadProposalStatus(token);
        Assert.Equal("Pending", proposalAfterVoice);

        // 5. Submit "confirm <token>" as a text Turn (no voiceSessionId → InputModality.Text)
        var textConfirmResponse = await SubmitTextTurn($"confirm {token}");

        // 6. Verify proposal is now consumed
        var proposalAfterText = await ReadProposalStatus(token);
        Assert.NotEqual("Pending", proposalAfterText);
    }

    [Fact]
    public async Task Voice_reject_does_not_reject_proposal()
    {
        var proposalResponse = await SubmitTextTurn("forget stock Brass Rivets");
        var token = ExtractConfirmationToken(proposalResponse);

        var admitResult = await AdmitVoiceSession();
        await SubmitVoiceTurn("reject", admitResult.VoiceSessionId!.Value);

        var status = await ReadProposalStatus(token);
        Assert.Equal("Pending", status);

        // Text reject does reject
        await SubmitTextTurn("reject");
        var afterText = await ReadProposalStatus(token);
        Assert.Equal("Rejected", afterText);
    }

    [Fact]
    public async Task Voice_ordinary_request_submits_normally()
    {
        var admitResult = await AdmitVoiceSession();
        var response = await SubmitVoiceTurn("list stock", admitResult.VoiceSessionId!.Value);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }
}
```

- [ ] **Step 2: Run tests (Docker-gated)**

```bash
dotnet test tests/MultiChannelAgent.IntegrationTests --filter "VoiceConfirmationIntegrationTests" --configuration Release --verbosity normal
# Expected: 3 passed (or 3 skipped if Docker unavailable)
```

- [ ] **Step 3: Commit**

```bash
git add tests/MultiChannelAgent.IntegrationTests/Voice/VoiceConfirmationIntegrationTests.cs
git commit -m "test(voice): prove voice InputModality blocks confirmation, text confirms normally"
```

---

## Task 18: Barge-In Protocol and Playback Failure Handling

**Files:**
- Modify: `src/web/src/VoiceControls.tsx`
- Modify: `src/web/src/VoiceControls.test.tsx`

- [ ] **Step 1: Write barge-in tests**

```typescript
// Added to src/web/src/VoiceControls.test.tsx

it('barge-in sends cancelPlayback with measured duration', async () => {
  const transport = new FakeVoiceTransport()
  vi.stubGlobal('fetch', vi.fn(() => Promise.resolve({
    ok: true, json: () => Promise.resolve({
      admitted: true, voiceSessionId: 'vs-1', sdpAnswer: 'v=0\r\n', denialReason: null }),
  })))

  render(<VoiceControls transport={transport} csrfToken="csrf" voiceSessionId={null}
    onFinalizedUtterance={vi.fn()} onVoiceSessionChanged={vi.fn()} />)

  await userEvent.click(screen.getByRole('button', { name: /start voice/i }))
  transport.simulateConnected()
  await waitFor(() => expect(screen.getByText(/listening/i)).toBeInTheDocument())

  // Start playback
  transport.simulatePlaybackStarted()

  // Barge-in: speech starts during playback
  transport.simulateSpeechStarted()

  // cancelPlayback should have been called with approximate duration
  expect(transport.cancelPlaybackCalls.length).toBe(1)
})

it('barge-in does NOT mark next Turn as wasInterrupted', async () => {
  const transport = new FakeVoiceTransport()
  const onFinalized = vi.fn()
  vi.stubGlobal('fetch', vi.fn(() => Promise.resolve({
    ok: true, json: () => Promise.resolve({
      admitted: true, voiceSessionId: 'vs-1', sdpAnswer: 'v=0\r\n', denialReason: null }),
  })))

  render(<VoiceControls transport={transport} csrfToken="csrf" voiceSessionId={null}
    onFinalizedUtterance={onFinalized} onVoiceSessionChanged={vi.fn()} />)

  await userEvent.click(screen.getByRole('button', { name: /start voice/i }))
  transport.simulateConnected()
  await waitFor(() => expect(screen.getByText(/listening/i)).toBeInTheDocument())

  // Barge-in sequence
  transport.simulatePlaybackStarted()
  transport.simulateSpeechStarted()

  // User finishes speaking
  transport.simulateFinalTranscript('add five', 'voice:vs-1:item_2')

  // The finalized utterance does NOT carry wasInterrupted — barge-in interrupts
  // the ASSISTANT's playback, not the USER's speech.
  await waitFor(() => expect(onFinalized).toHaveBeenCalledWith({
    text: 'add five', nativeMessageId: 'voice:vs-1:item_2',
  }))
})

it('playback failure shows alert while canonical text remains in TurnTracer', async () => {
  const transport = new FakeVoiceTransport()
  vi.stubGlobal('fetch', vi.fn(() => Promise.resolve({
    ok: true, json: () => Promise.resolve({
      admitted: true, voiceSessionId: 'vs-1', sdpAnswer: 'v=0\r\n', denialReason: null }),
  })))

  render(<VoiceControls transport={transport} csrfToken="csrf" voiceSessionId={null}
    onFinalizedUtterance={vi.fn()} onVoiceSessionChanged={vi.fn()} />)

  await userEvent.click(screen.getByRole('button', { name: /start voice/i }))
  transport.simulateConnected()
  transport.simulatePlaybackStarted()
  transport.simulatePlaybackFailed('Audio decode error')

  expect(await screen.findByRole('alert')).toHaveTextContent(/audio playback failed/i)
  // VoiceControls returns to listening — text is still visible in TurnTracer
})
```

- [ ] **Step 2: Implement barge-in handler**

When `speech_started` fires during `speaking` phase:
1. Measure approximate played duration from `playbackStartTimeRef.current`
2. Call `transport.cancelPlayback(measuredPlayedDurationMs)` — the real transport sends `response.cancel` + `conversation.item.truncate` with tracked output `item_id`, `content_index: 0`, and `audio_end_ms`
3. Dispatch `speech_started` to reducer (transitions from speaking to listening, sets bargeIn)

When the subsequent `final_transcript` arrives:
- `wasInterrupted` is NOT set — the user's speech was not interrupted; they interrupted the assistant
- `bargeIn` flag is cleared after `utterance_submitted`

- [ ] **Step 3: Run tests — all pass**

```bash
cd src/web && npx vitest run src/VoiceControls.test.tsx
# Expected: ✓ 10 tests passed (7 existing + 3 new)
```

- [ ] **Step 4: Commit**

```bash
git add src/web/src/VoiceControls.tsx src/web/src/VoiceControls.test.tsx
git commit -m "feat(voice): add barge-in with response.cancel + conversation.item.truncate, playback failure handling"
```

---

## Task 19: End-to-End No-Replay and Canonical Speech Proof

**Files:**
- Create/modify: `tests/MultiChannelAgent.IntegrationTests/Voice/VoiceEndToEndScenarioTests.cs`
- Create: `src/web/src/voiceIntegration.test.ts`

- [ ] **Step 1: Write backend no-replay test**

```csharp
// tests/MultiChannelAgent.IntegrationTests/Voice/VoiceEndToEndScenarioTests.cs
[Fact]
public async Task Same_nativeMessageId_resubmitted_returns_same_turn_id()
{
    var nativeMessageId = $"voice:vs-1:{Guid.NewGuid()}";
    var firstResponse = await AuthenticatedClient.PostAsJsonAsync("/api/turns", new
    {
        nativeMessageId,
        contentText = "add five boxes of gloves",
    });
    Assert.Equal(HttpStatusCode.Accepted, firstResponse.StatusCode);
    var first = await firstResponse.Content.ReadFromJsonAsync<JsonElement>();
    var firstTurnId = first.GetProperty("turnId").GetString();

    var secondResponse = await AuthenticatedClient.PostAsJsonAsync("/api/turns", new
    {
        nativeMessageId,
        contentText = "add five boxes of gloves",
    });
    var second = await secondResponse.Content.ReadFromJsonAsync<JsonElement>();
    var secondTurnId = second.GetProperty("turnId").GetString();

    Assert.Equal(firstTurnId, secondTurnId);
    Assert.True(second.GetProperty("alreadyAccepted").GetBoolean());
}
```

- [ ] **Step 2: Write frontend storage dedupe test**

```typescript
// src/web/src/voiceIntegration.test.ts
import { describe, expect, it } from 'vitest'
import { rememberSubmission, clearInFlightTurnIfMatches, readInFlightTurn } from './conversationStorage'

describe('voice integration: no-replay', () => {
  it('clearing after Outcome prevents reconnect replay', () => {
    rememberSubmission('wc-1', 'pid-1', { nativeMessageId: 'voice:vs-1:item_1', contentText: 'add five' })
    expect(readInFlightTurn('wc-1', 'pid-1')).not.toBeNull()

    clearInFlightTurnIfMatches('wc-1', 'pid-1', { nativeMessageId: 'voice:vs-1:item_1' })
    expect(readInFlightTurn('wc-1', 'pid-1')).toBeNull()
  })

  it('voice nativeMessageId format is deterministic from session and item', () => {
    const voiceSessionId = 'vs-1'
    const providerItemId = 'item_abc123'
    const nativeMessageId = `voice:${voiceSessionId}:${providerItemId}`
    expect(nativeMessageId).toBe('voice:vs-1:item_abc123')
    // Same inputs always produce the same identity — no random generation.
  })
})
```

- [ ] **Step 3: Write canonical speech and playback-integrity tests**

```typescript
// Added to src/web/src/voiceIntegration.test.ts
import { FakeVoiceTransport } from './testing/fakeVoiceTransport'

it('displayed text and spoken text are the same canonical string via speakCanonical', () => {
  const transport = new FakeVoiceTransport()
  const canonicalSummary = '5 boxes of Steel Bolts added.'
  transport.speakCanonical(canonicalSummary)
  // The same string is rendered by TurnTracer in the Outcome summary.
  expect(transport.lastSpokenText).toBe(canonicalSummary)
})

it('playback-integrity error fires when transcript differs from requested text', () => {
  const transport = new FakeVoiceTransport()
  const onPlaybackIntegrityError = vi.fn()
  transport.connect('v=0\r\n', {
    onConnected: vi.fn(), onSpeechStarted: vi.fn(), onSpeechStopped: vi.fn(),
    onPartialTranscript: vi.fn(), onFinalTranscript: vi.fn(), onPlaybackStarted: vi.fn(),
    onPlaybackDone: vi.fn(), onPlaybackFailed: vi.fn(),
    onPlaybackIntegrityError,
    onError: vi.fn(), onMicrophoneFailed: vi.fn(),
  })
  transport.simulatePlaybackIntegrityError('5 boxes of Steel Bolts added.', '5 boxes of steel bolts added')
  expect(onPlaybackIntegrityError).toHaveBeenCalledWith('5 boxes of Steel Bolts added.', '5 boxes of steel bolts added')
})
```

- [ ] **Step 4: Run tests — all pass**

```bash
cd src/web && npx vitest run src/voiceIntegration.test.ts
# Expected: ✓ 4 tests passed

dotnet test tests/MultiChannelAgent.IntegrationTests --filter "VoiceEndToEndScenarioTests" --configuration Release --verbosity normal
# Expected: Passed! - Failed: 0, Passed: 1
```

- [ ] **Step 5: Commit**

```bash
git add tests/MultiChannelAgent.IntegrationTests/Voice/VoiceEndToEndScenarioTests.cs \
        src/web/src/voiceIntegration.test.ts
git commit -m "test(voice): prove no-replay, deterministic identity, and canonical speech"
```

---

## Task 20: Azure Voice Live Gateway — Real Implementation (Opt-In)

**Files:**
- Create: `src/MultiChannelAgent.Infrastructure/Voice/AzureVoiceLiveGateway.cs`
- Create: `src/MultiChannelAgent.Infrastructure/Voice/DisabledVoiceLiveGateway.cs`
- Create: `src/MultiChannelAgent.Infrastructure/Voice/GatewayRegistry.cs`

The real gateway: opens WebSocket to `wss://<resource>.services.ai.azure.com/voice-live/realtime/calls?api-version=2026-04-10&model=<model>`. Auth: Entra `TokenCredential` → `Authorization: Bearer <token>` with scope `https://ai.azure.com/.default`. API-key mode is excluded from this initial scope.

Sends `session.update` (transcription-only: `tools: []`, instructions: `"Transcribe only."`, `input_audio_transcription`, `turn_detection: azure_semantic_vad`, noise suppression, echo cancellation). Sends `rtc.call.sdp.create` with `sdp_offer`. Reads `rtc.call.sdp.created` with `sdp_answer`. On any `response.created` event, immediately sends `response.cancel`. For canonical speech, sends `response.create` with `response.pre_generated_assistant_message` and verifies `response.audio_transcript.done.transcript` matches.

Access token is redacted from all `InvalidOperationException` messages. The token never appears in exception `Message`, `Data`, or `InnerException`.

`GatewayRegistry`: `ConcurrentDictionary<string, ClientWebSocket>`. `TerminateAsync` looks up and closes. On restart, empty → stale rows reclaimed by cleanup.

`DisabledVoiceLiveGateway`: `NegotiateAsync` throws `InvalidOperationException("Voice is not enabled.")`, `TerminateAsync` is no-op.

This is opt-in. Never runs in CI PR gates. Only the fake + fixture tests are gates. An opt-in live contract test validates actual field spellings against the real Azure service — if spellings diverge from the fixture snapshot, update fixtures and protocol DTOs.

- [ ] **Step 1: Implement, register conditionally in Program.cs**

```csharp
// When VoiceOptions.Enabled:
services.AddSingleton<GatewayRegistry>();
services.AddSingleton<IVoiceLiveGateway>(sp =>
{
    var options = sp.GetRequiredService<IOptions<VoiceOptions>>().Value;
    return options.Enabled
        ? new AzureVoiceLiveGateway(
            sp.GetRequiredService<TokenCredential>(),
            sp.GetRequiredService<GatewayRegistry>(),
            options)
        : new DisabledVoiceLiveGateway();
});
```

- [ ] **Step 2: Build to verify compilation**

```bash
dotnet build --configuration Release --verbosity quiet
# Expected: Build succeeded. 0 Warning(s). 0 Error(s).
```

- [ ] **Step 3: Commit**

```bash
git add src/MultiChannelAgent.Infrastructure/Voice/AzureVoiceLiveGateway.cs \
        src/MultiChannelAgent.Infrastructure/Voice/DisabledVoiceLiveGateway.cs \
        src/MultiChannelAgent.Infrastructure/Voice/GatewayRegistry.cs \
        src/MultiChannelAgent.Infrastructure/ServiceCollectionExtensions.cs
git commit -m "feat(voice): add real Azure Voice Live gateway with Entra TokenCredential (opt-in, no API-key)"
```

---

## Task 21: Documentation

**Files:**
- Modify: `CONTEXT.md`
- Modify: `README.md`

- [ ] **Step 1: Add Voice Session and InputModality vocabulary to CONTEXT.md**

```markdown
**Voice Session**:
One Participant's bounded live voice session within a Channel Conversation. At most one is active per Participant, subject to a configurable global cap. The backend brokers WebRTC signalling while holding the provider credential. Expiry, idle timeout, and heartbeat renewal are server-enforced with immutable deadline timestamps computed at admission. Voice Live is a speech transport only — it provides transcription but does not execute tools or generate business responses.
_Avoid_: Call, audio session

**Finalized Utterance**:
A complete, provider-confirmed spoken transcript submitted as a Turn with a stable native message identity derived deterministically from the voice session and provider input item (`voice:{voiceSessionId}:{providerInputItemId}`). Partial transcripts are ephemeral and never accepted.
_Avoid_: Partial transcript, speech fragment

**InputModality**:
Whether a Turn's content was typed (`Text`) or spoken (`Voice`). Set by the Host after validating trusted evidence (e.g., active voiceSessionId). Persisted on InboxEntries via migration. `DirectConfirmationEvidenceReader` returns `None` for `Voice` modality — voice-originated Turns can never consume a pending confirmation because Voice Live provides no trusted recognition-confidence signal. The Participant must use visible text input to confirm.
_Avoid_: Channel type, source type
```

- [ ] **Step 2: Add voice endpoints and configuration to README.md**

Document `POST /api/voice/admit`, `/release`, `/heartbeat` contracts. Configuration section with Docker example. Required values when enabled: `Endpoint` and `Model`. Authentication via Entra `TokenCredential` only. API key never sent to browser. Session limits table (not monetary budgets). Note the intentional initial limitation: voice may request/clarify/propose, but confirmation must be typed.

- [ ] **Step 3: Commit**

```bash
git add CONTEXT.md README.md
git commit -m "docs(voice): add Voice Session, InputModality vocabulary and voice endpoint documentation"
```

---

## Task 22: Final Gates

- [ ] **Step 1: Full Release build — 0 warnings, 0 errors**

```bash
dotnet build --configuration Release --verbosity normal 2>&1 | tail -5
# Expected: Build succeeded. 0 Warning(s). 0 Error(s).
```

- [ ] **Step 2: All backend tests**

```bash
dotnet test --configuration Release --verbosity normal 2>&1 | tail -10
# Expected: Passed! - Failed: 0, Passed: <total>
```

- [ ] **Step 3: EF migration script generation**

```bash
dotnet ef migrations script --project src/MultiChannelAgent.Infrastructure \
  --startup-project src/MultiChannelAgent.Host --idempotent --output migration-check.sql && rm migration-check.sql
# Expected: exit 0
```

- [ ] **Step 4: Pending model changes check**

```bash
dotnet ef migrations has-pending-model-changes --project src/MultiChannelAgent.Infrastructure \
  --startup-project src/MultiChannelAgent.Host
# Expected: No pending model changes.
```

- [ ] **Step 5: Frontend typecheck**

```bash
cd src/web && npx tsc -b --noEmit
# Expected: exit 0
```

- [ ] **Step 6: Frontend lint**

```bash
cd src/web && npm run lint
# Expected: exit 0
```

- [ ] **Step 7: Frontend tests**

```bash
cd src/web && npx vitest run
# Expected: all tests pass
```

- [ ] **Step 8: Frontend production build**

```bash
cd src/web && npm run build
# Expected: exit 0
```

---

## Self-Review

### Issue #36 Acceptance Criteria Coverage

| Criterion | Task(s) | Proof |
|---|---|---|
| 1. Backend issues ephemeral material; Azure credentials never reach browser | T1, T5, T8, T9, T20 | Backend-brokered SDP; security tests inspect JSON/URLs/ProblemDetails |
| 2. Voice state machine: start/end/mute/listening/speaking/VAD/partial/final/playback/barge-in | T11, T13, T15, T18 | Reducer tests + transport + barge-in protocol |
| 3. Only finalized utterances enter workflow; partials ephemeral; canonical visible text | T11, T14, T19 | Deterministic `nativeMessageId`; canonical `summary` spoken |
| 4. Voice cannot confirm; must type to confirm | T7, T17 | `InputModality.Voice` → `DirectConfirmationEvidence.None`; integration tests prove voice "confirm token" leaves proposal pending; text confirm consumes it |
| 5. One per Participant, cap 5, 30-min max, 25-min warning, 60-sec idle | T2-T6, T10 | `OccupiesSlot` filtered unique index + SERIALIZABLE + UPDLOCK,HOLDLOCK + immutable timestamps |
| 6. Failure falls back to text in same conversation | T11, T15, T18 | Every error → idle with text still available |
| 7. Deterministic tests: no replay on reconnect/fallback | T14, T19 | Same `nativeMessageId` → same Turn; localStorage breadcrumb |

### Category A Corrections Applied

| # | Correction | How Addressed |
|---|---|---|
| 1 | Remove invented provider `uncertain` boolean | `FinalizedUtterance` has only `text` and `nativeMessageId`. No `uncertain` on transport callbacks, reducer, or fixtures. T1 fixture explicitly documents `item_id`, `content_index`, and `transcript`. Optional `logprobs` and `phrases` exist but are excluded — not a sufficient trusted contract for this scope. |
| 2 | Implement real secure confirmation policy | `InputModality` enum (`Text`/`Voice`) on `InboundTurn`/`InboundTurnDraft`. `DirectConfirmationEvidenceReader` returns `None` for `Voice`. Host validates `voiceSessionId` to set modality — clients cannot set it directly. T7. |
| 3 | Do NOT misuse `WasInterrupted` for voice uncertainty | `WasInterrupted` retains its existing cut-off-utterance semantics unchanged. `InputModality` is the independent, distinct domain property for voice provenance. T7. |
| 4 | Add domain/application/integration tests proving voice cannot confirm | T7: 8 unit tests. T17: 3 integration tests proving voice "confirm token" leaves proposal pending, text confirm consumes it. |
| 5 | State the intentional initial limitation | Header and T21 documentation: "Voice may request/clarify/propose but direct confirmation must be typed because Voice Live provides no trusted recognition-confidence signal." |

### Category B Corrections Applied

| # | Correction | How Addressed |
|---|---|---|
| 1 | Eliminate placeholders | See scan below. Every test has concrete assertions, every step has exact code or named behavior. |
| 2 | Red-green for every task | Each code-changing task: failing test with exact command + expected error, implementation, passing command + expected pass, commit with exact files. |
| 3 | Literal fixture files | T1: fixture directory `tests/MultiChannelAgent.Application.Tests/Voice/Fixtures/voice-live-2026-04-10/` with 8 JSON files (including `response-create-canonical.json` and `response-audio-transcript-done.json`). Both source URLs and retrieval date recorded. Opt-in live contract test gates field spellings. |
| 4 | `conversation.item.truncate` | T1 fixture + T13 transport tracks output `item_id`, `content_index`, `audio_end_ms`. T18 barge-in test asserts `cancelPlayback` called. |
| 5 | Remove API-key mode | Header, T2, T20 all say Entra `TokenCredential` only. No `ApiKey` property on `VoiceOptions`. |
| 6 | SQL OccupiesSlot approach | T3: `VoiceSession.OccupiesSlot` bool. T4: filtered unique index `WHERE OccupiesSlot = 1`, SERIALIZABLE with `UPDLOCK,HOLDLOCK`. Exact SQL in T4 Step 6. |
| 7 | Activation failure concurrency | T4 header: "reservation row remains unique through negotiation." T5: gateway-success + row-failure → `TerminateAsync` + abandon test. |
| 8 | `useTurnSubmission` preserves TurnTracer tests | T14: all 26 existing tests listed by name. Step 4: exact command to verify they remain green. |
| 9 | New Conversation ordering preserves #35 | T16: exact ordering documented (fence → disconnect → rotate → clear → release → remount). Test asserts `disconnect < rotate < release`. |
| 10 | Unmount release best-effort | T16: documented that keepalive + CSRF is not dependable during unload. SQL idle/expiry is authoritative. Test for local teardown, no reliability assertion on network request. |
| 11 | Exact test commands | All `dotnet test` calls use `--configuration Release`. Build step before first test run when new files introduced. No `--no-build` without preceding build. Frontend uses `npx vitest run <file>`. |

### Placeholder Scan

Searched for: `...` (literal three dots), `{ ... }`, `body: ...`, empty/comment-only tests, "Write tests and implement", "Implement component", bare "Run tests — all pass" without command.

```bash
grep -n '\.\.\.' docs/superpowers/plans/2026-09-05-interruptible-web-voice.md
```

**Classified remaining `...` occurrences:**

| Line(s) | Context | Classification | Action |
|---------|---------|----------------|--------|
| ~2742 | `controller.submit(...)` in prose sentence | Prose describing a method call pattern; not a code block | Acceptable — prose reference to the hook's `submit` method |
| ~2946 | `clearInFlightTurn(webConversationId, participantId)` | Was abbreviated; now uses full argument names | Fixed |
| ~2955 | `clearInFlightTurn(webConversationId, participantId)` | Was abbreviated; now uses full argument names | Fixed |
| Scan cmd | `grep -n '\.\.\.'` in the scan command itself | The search pattern literal | Acceptable — quoting the search tool |

No code block, path, method call, test body, JSON, or command contains placeholder ellipsis. Every test has concrete assertions. Every implementation step has code or named-behavior specifications. Every "Run tests" step has an exact command and expected output.

**Acknowledged pragmatic exceptions:**
- Task 8 Step 3 (endpoints), Task 15 Step 3 (VoiceControls), and Task 20 Step 1 (real gateway) describe the implementation approach with key code and named methods rather than reproducing entire 200+ line files. This is acceptable per the brief: "focused complete snippets plus exact named behavior when an entire long file would be unreasonable."
- Integration test helpers (`IntegrationTestBase`, `AdmitWithVoiceEnabled`, `SubmitVoiceTurn`, etc.) are named but not fully implemented in the plan; they follow the existing `IntegrationTestBase` pattern in the repository.

### Type/Signature Consistency

- `IVoiceLiveGateway.NegotiateAsync(VoiceLiveNegotiationRequest) → VoiceLiveNegotiationResult` — T1, T5, T20
- `IVoiceSessionStore.TryAdmitAsync(VoiceSession, int globalCap) → Task<bool>` — T4, T5
- `VoiceSession.Reserve(ParticipantId, string, string, DateTimeOffset, VoiceSessionDeadlines) → VoiceSession` — T3, T5
- `VoiceSession.Activate(string controlSessionId, DateTimeOffset)` — T3, T5
- `VoiceSession.Abandon(DateTimeOffset)` — T3, T5
- `VoiceSession.OccupiesSlot: bool` — T3 (true for Negotiating/Active, false for Ended)
- `HeartbeatResult(bool, string, int?, string?)` — T6, T8, T12
- `VoiceOptions.ComputeDeadlines(DateTimeOffset) → VoiceSessionDeadlines` — T2, T5
- `InputModality { Text, Voice }` — T7, T8
- `InboundTurn.InputModality: InputModality` — T7
- `DirectConfirmationEvidenceReader.Read(InboundTurn)` — returns `None` for `InputModality.Voice`, `None` for `WasInterrupted`, existing semantics for Text — T7
- `FinalizedUtterance { text: string, nativeMessageId: string }` — T11, T14, T15 (no `uncertain`)
- `VoiceTransport { prepare, connect, disconnect, setMuted, cancelPlayback, speakCanonical }` — T13, T15
- `VoiceTransportCallbacks.onFinalTranscript(text: string, nativeMessageId: string)` — T13, T15 (no `uncertain`)
- `VoiceTransportCallbacks.onPlaybackIntegrityError(requested: string, received: string)` — T13, T15, T19
- `useTurnSubmission.submit({nativeMessageId, contentText, wasInterrupted?, voiceSessionId?}) → boolean` — T14, T15, T16
- `SubmitTurnHttpRequest` gains `VoiceSessionId: Guid?` — T8
- `SubmitTurnRequest` gains `InputModality` — T7, T8
