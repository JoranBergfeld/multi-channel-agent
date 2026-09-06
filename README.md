# Multi-Channel Agent

A single deployable ASP.NET Core + React application that accepts a normalized Turn, persists it
through production-style SQL migrations and hosted workers, and exposes its recorded terminal
Outcome through one application boundary ([issue #27](https://github.com/JoranBergfeld/multi-channel-agent/issues/27)).
It also implements the signed-in web BFF session contract and the first slice of the Inventory
domain - explicit Inventory creation, listing, and selection
([issue #28](https://github.com/JoranBergfeld/multi-channel-agent/issues/28)). It delivers the first
real conversational Inventory path: a signed-in web Participant converses ("list stock", "find
&lt;name&gt;"), a scripted Foundry-double boundary proposes a bounded `list_stock`/`find_stock` tool
call, and a trusted application tool dispatcher - never the model itself - executes it against SQL
under a freshly rechecked authorization, returning a typed semantic result the web renders and the
Inventory workspace refetches its authoritative Stock projection from
([issue #30](https://github.com/JoranBergfeld/multi-channel-agent/issues/30)). See the parent spec
(issue #26) and the domain vocabulary in `CONTEXT.md`.

## Solution layout

```text
src/
  MultiChannelAgent.Domain/          Pure domain model: the InboundTurn contract (typed ChannelPrincipal
                                      evidence, ordered content parts with provenance, declared channel
                                      capabilities), Outcome with its semantic category, Delivery, the
                                      Inventory aggregate (Participant, Inventory, Membership, Unit,
                                      ActiveInventorySelection), and the Stock Entry model (Location,
                                      StockEntry, Quantity, StockEntrySummary, the deterministic order
                                      key, the shape-bound list cursor, and the Find candidate
                                      outcome). No dependencies.
  MultiChannelAgent.Application/     Application boundary: TurnAcceptanceService, TurnProcessingCoordinator,
                                      DeliveryDispatchCoordinator, TurnOutcomeReader, the scripted model
                                      boundary and its ToolCallProposal/IToolDispatcher seam, the trusted
                                      TurnExecutionContext/TurnExecutionContextFactory, the Inventory
                                      bootstrap/creation/listing/selection services, the authorized
                                      StockListingService/StockFindingService/StockToolDispatcher, and
                                      the repository/lease/model/delivery abstractions Infrastructure
                                      implements. Depends only on Domain.
  MultiChannelAgent.Infrastructure/  EF Core SQL Server DbContext, entity configurations, production
                                      migrations, and the SQL-backed repositories/lease coordinator,
                                      including SqlStockStore and SqlFoundryConversationBindingStore.
                                      Depends on Domain and Application.
  MultiChannelAgent.Host/            ASP.NET Core: HTTP endpoints, health checks, hosted workers
                                      (Turn processing, Delivery dispatch, Outcome payload cleanup),
                                      the Entra/Test authentication contract, CSRF-protected session,
                                      Inventory, and Stock projection endpoints, and the published
                                      React/Vite client (wwwroot). Depends on all of the above.
  web/                               React + TypeScript + Vite client: signed-in onboarding, Inventory
                                      create/list/select, a resumable streaming conversation (rendering
                                      typed List/Find results), conversation rotation, and a responsive
                                      live Inventory workspace.
tests/
  MultiChannelAgent.Domain.Tests/         Pure unit tests for domain types.
  MultiChannelAgent.Application.Tests/    Application-layer tests against in-memory fakes and the
                                           scripted model boundary/controllable clock.
  MultiChannelAgent.ArchitectureTests/     NetArchTest layering/dependency-direction checks.
  MultiChannelAgent.IntegrationTests/      HTTP-boundary scenarios (SQLite-backed, Docker-free) plus
                                           the SQL-backed application-boundary scenarios: ephemeral
                                           Testcontainers SQL Server, production EF Core migrations,
                                           and real HTTP round trips through the Host.
```

Dependency direction is enforced both by project references (a reversed reference is a build-time
circular-dependency error) and by `MultiChannelAgent.ArchitectureTests`.

## Prerequisites

- .NET 10 SDK
- Node.js 22+ and npm (for the web client)
- Docker (only required for the SQL Server-backed subset of `MultiChannelAgent.IntegrationTests`; see below if Docker is unavailable)

## Build

```bash
dotnet restore
dotnet build --configuration Release
```

```bash
cd src/web
npm ci
npm run build   # tsc -b && vite build; output in src/web/dist
```

## Test

```bash
# Pure domain/application unit tests, architecture checks, fast SQLite-backed HTTP scenarios, and
# (if Docker is available) the SQL-backed application-boundary scenarios, in one pass:
dotnet test

# Just the web client:
cd src/web && npm run lint && npm run build
```

`MultiChannelAgent.IntegrationTests` boots an ephemeral SQL Server container via Testcontainers,
applies the production EF Core migrations, and submits a synthetic Turn (and, separately, drives the
signed-in Inventory scenario) through the real HTTP endpoints, driving the hosted-worker duties
deterministically (via their internal one-shot operations, not sleeps) instead of waiting on the
periodic background loop. Whether these tests may skip is governed by an explicit environment
contract, not by broadly catching container startup failures:

- **`REQUIRE_DOCKER_TESTS=true`** (set by CI) removes the ability to skip: the Docker daemon is
  positively probed before any container is built, and if it is unreachable the tests **fail** rather
  than silently skip. Any failure while bringing up the container once the daemon is confirmed
  reachable (bad image, bad configuration, a broken migration) also fails the tests.
- Locally, with the variable unset (or not `"true"`), the same positive daemon probe is used: only a
  genuinely unreachable daemon causes a clean **Skipped** result with a clear reason. Any other
  failure once the daemon answers still fails the tests, so a real bug is never masked as "Docker is
  unavailable".

## Run locally

Requires a reachable SQL Server (see `ConnectionStrings:MultiChannelAgent` in `appsettings.json`, or
set `ConnectionStrings__MultiChannelAgent`) with migrations applied:

```bash
export PATH="$PATH:$HOME/.dotnet/tools"
dotnet tool install --global dotnet-ef --version 10.0.11   # once
dotnet ef database update \
  --project src/MultiChannelAgent.Infrastructure \
  --startup-project src/MultiChannelAgent.Infrastructure \
  -- --connection-string "<your SQL Server connection string>"

dotnet run --project src/MultiChannelAgent.Host
```

### Authentication

`Authentication:Provider` selects exactly one identity provider and fails fast at startup if it is
missing or misconfigured - there is no silent fallback:

- **`Entra`** (the `appsettings.json` default): the real single-tenant Microsoft Entra
  authorization-code flow. Requires `Authentication:Entra:TenantId`, `ClientId`, and `ClientSecret`
  to all be configured; starting without them throws immediately.
- **`Test`** (the `appsettings.Development.json` default, and what `dotnet run` uses locally):
  a deterministic double. Refused outright when `ASPNETCORE_ENVIRONMENT=Production`. Sign in by
  `POST`ing to `/api/test/sign-in` with `{ "participantId": "<a GUID>", "displayName": "...",
  "activeTenantMember": true }`; the response's `Set-Cookie` carries the real session cookie used by
  every other endpoint, exactly like a real sign-in would.

Every authenticated session is Secure, HttpOnly, SameSite=Lax. Every mutating endpoint additionally
requires the `X-CSRF-TOKEN` header set to the `csrfToken` returned by `GET /api/session/bootstrap`.

#### Tenant member directory (Microsoft Graph)

Membership grants, ownership transfer, and orphan recovery all resolve their target through a single
`ITenantMemberDirectory` seam. In `Entra` mode this is `GraphTenantMemberDirectory`, a thin adapter
over Microsoft Graph v1.0 (`GET /users/{id}` and a `$filter`-based address lookup) authenticated with
an app-only `https://graph.microsoft.com/.default` token:

- By default it reuses `Authentication:Entra:TenantId`/`ClientId`/`ClientSecret` (already required
  above) to build a `ClientSecretCredential`. Grant the app registration the Graph **application**
  permission `User.Read.All` (admin-consented) so it can resolve any tenant member.
- Set `Authentication:Entra:UseManagedIdentityForGraph=true` to use `DefaultAzureCredential` (managed
  identity when deployed to Azure) instead - no client secret required in that case.
- Credential construction and configuration validation are eager and local (no network call), but the
  actual Graph token/HTTP call only ever happens the first time something resolves a tenant member -
  never at startup, and never for `/health/live` or `/health/ready`.
- A Graph outage or authorization failure (401/403/5xx/timeout/network error) surfaces as a visible
  failure rather than silently treating every Inventory as orphaned; only a definitive 404, a
  disabled/guest account, or an ambiguous address match is treated as "not found".
- **`Test`** mode never contacts Graph at all - it substitutes `TestTenantMemberDirectory`, controlled
  entirely through `/api/test/sign-in` and `/api/test/tenant-directory/register`.

Then:

- `GET /health/live` — liveness (no dependencies).
- `GET /health/ready` — readiness (checks SQL Server connectivity).
- `GET /api/session/bootstrap` — the authenticated session: canonical Participant, authorized
  Inventories, the current Active Inventory (if any), an onboarding flag, and a fresh CSRF token.
  `401` unauthenticated, `403` for an authenticated but inactive/non-member identity.
- `POST /api/inventories` — explicitly create a named Inventory:
  `{ "name": "...", "clientRequestId": "<a stable idempotency key>" }`. The requester atomically
  becomes Owner, and the Inventory is created with the reserved `each` Unit and its fixed aliases.
  Resubmitting the same `clientRequestId` returns the original Inventory.
- `GET /api/inventories` — list only the Inventories the caller is authorized for.
- `POST /api/inventories/{inventoryId}/select` — explicitly switch the Active Inventory for the
  current web conversation. `404` (never a distinct signal) when the Inventory does not exist or is
  not authorized for the caller; selecting never itself grants access.
- `GET /api/inventory-events` — a Participant-level server-sent event stream that begins with a
  complete snapshot of every authorized Inventory's current version, then reports changed versions
  and revoked access. Reconnecting starts another complete snapshot, so the web workspace
  resynchronizes without replaying a retained change history.
- `GET /api/inventories/{inventoryId}/stock` — the authoritative Stock projection the Inventory
  workspace refetches. It is the same authorized read the conversational `list_stock` tool call
  performs, with the same bounds: `includeZero`, `nameFilter`, `unit` (an opaque Unit id, exact
  canonical name, or active alias), `locationId` (an opaque Location id or exact name), `unlocated`,
  `pageSize` (1-50), and `cursor`. `404` (never a distinct signal) when the Inventory does not exist
  or is not authorized; `400` naming the parameter at fault for an unknown Unit/Location, an
  out-of-bounds page size, or a cursor issued for a differently shaped request.
- `POST /api/turns` — submit a normalized `InboundTurn`:
  `{ "nativeMessageId": "...", "contentText": "...", "locale": "en-US", "traceId": "..." }`. Participant,
  ChannelConversation, channel, typed principal evidence, and channel capabilities all come from the
  authenticated session and the web adapter - never from the body. Returns `202 Accepted` with
  `{ "turnId": "...", "alreadyAccepted": false }`. Resubmitting the same `nativeMessageId` (within the
  same Participant and conversation - the scope a native id is actually unique in) never duplicates
  acceptance or reprocesses: it returns `202` while the Turn is still being processed, and `200` with
  that Turn's recorded terminal Outcome once it has one.
- `GET /api/turns/{turnId}/events` — a finite server-sent event stream for one authorized Turn,
  publishing stable event IDs for accepted, processing, typed semantic parts, and the terminal
  Outcome. `Last-Event-ID` resumes strictly after an issued ID; the stream closes after the terminal
  event. A missing Turn and another Participant's Turn both return the same `404`.
- `GET /api/turns/{turnId}/outcome` — the recorded terminal Outcome and its Deliveries once hosted
  processing completes (`404` until then).
- `POST /api/conversation/new` — advance this browser profile's Channel Conversation to a fresh
  Conversation Generation and settle pending clarification or confirmation from the prior
  generation. Authorized Inventories and the Active Inventory are preserved. Requires the session's
  CSRF token.

An Outcome reports both `status` - whether processing produced an answer at all - and `category`, the
semantic shape of that answer (`completed`, `ambiguous`, `not_found`, `forbidden`, `conflict`,
`invalid`, `confirmation_required`, `transient_failure`). A deterministic answer such as "nothing
matched" or "select an Inventory first" is completed processing with its own category; `status` is
only `failed` when the system, the model, or a dependency failed to answer. Answers also record one
channel-neutral response part as a Delivery, dispatched and retried independently of processing, and
a typed result payload is retained for 24 hours (a scheduled cleanup pass discards it afterwards -
current SQL state is authoritative, the payload is only a convenience for resuming an answer).

The scripted model boundary (`ScriptedModelBoundary`) stands in for a Foundry-backed model and
understands a small bounded grammar, always under the trusted context the application injects:

```text
list stock [including zero] [named <text>] [unit <unit>] [in <location>] [unlocated]
           [page size <n>] [after <cursor>]
find <name or Stock Entry id> [unit <unit>] [in <location>] [unlocated]
```

Unit and Location references resolve exactly - by opaque identifier, exact name, or (for a Unit) an
active alias - and an unknown one is answered `reference_not_found` rather than created or ignored.
A page's `nextCursor` may only be resumed by an identically shaped request. Anything else is echoed
back as one Delivery; a Turn whose content is exactly `trigger-scripted-failure` produces a failed
Outcome with no Delivery, so both terminal paths are reproducible without any external model
dependency.

## Voice

Voice adds an optional real-time speech path for web Participants. It is **disabled by default**
(`Voice:Enabled=false`) and requires explicit configuration; all other features remain fully
functional without it.

### Architecture

The browser creates a WebRTC peer connection (via `RTCPeerConnection` and `getUserMedia`) and
submits the resulting SDP offer to the backend via `POST /api/voice/admit`. The backend brokers the
WebRTC negotiation through the Azure Voice Live WebSocket and returns the SDP answer — the browser
never contacts Azure directly. Provider credentials and the internal control session identifier are
never returned to the browser, included in any response, or logged.

Voice Live transcribes incoming audio and synthesises the exact canonical Outcome summary text sent
by the application (`speakCanonical`). The audio transcript is integrity-checked against the
requested text after playback. Voice Live does not execute tools or make business decisions; all
tool dispatch and Inventory operations remain inside the trusted application boundary, identical to
text Turns.

### Voice UX

The browser renders **Start Voice**, **Mute/Unmute**, and **End Voice** controls. Phases progress
`idle → requesting → connecting → listening ↔ speaking → ending`. While a session is active the
UI shows:

- Phase status (listening, speaking, requesting, connecting).
- **Partial transcript** — ephemeral text displayed as speech is detected; cleared when a
  Finalized Utterance is received or speech is interrupted.
- **Warnings** — surfaced once when the session warning threshold is crossed with time remaining.
- **Errors and playback failures** — inline, without raw provider detail.

Server-side VAD (`azure_semantic_vad`) detects speech boundaries. If the user speaks while the
assistant is playing back a response, a barge-in is signalled: playback is truncated immediately
and the user's speech is processed normally.

The canonical Outcome summary text is **always kept visible** in the conversation regardless of
playback state. If playback fails or the received audio transcript does not match the requested
text (integrity check), the UI surfaces the failure and the session continues from the text display
— no separate voice fallback is attempted.

Only **Finalized Utterances** (provider-confirmed transcriptions) are submitted as Turns. Partial
speech is ephemeral and never submitted. Each Finalized Utterance's `nativeMessageId` is
deterministically derived as `voice:{voiceSessionId}:{providerItemId}` — where `providerItemId`
is the provider's `item_id` from the
`conversation.item.input_audio_transcription.completed` event — and cannot be replayed.

### Intentional confirmation limitation

Voice Turns may ask questions, request operations, and propose changes exactly like text Turns.
However, a voice Turn **cannot confirm or reject a pending proposal**. Even when the spoken words
match the confirmation vocabulary ("yes", "confirm", etc.), the Turn yields no confirmation
evidence and the proposal remains pending.

To confirm or reject a pending proposal, the Participant must submit a **typed Turn using visible
text**.

**Why:** Voice Live provides no trusted recognition-confidence signal; voice-based confirmation
would be unverifiable. The limitation ensures business mutations are only triggered by input the
Participant can observe directly.

### Endpoints

All voice endpoints require authentication (`ActiveTenantMember` policy) and the `X-CSRF-TOKEN`
antiforgery header (the same token returned by `GET /api/session/bootstrap`). No response, error,
or log ever contains a provider credential, internal control session ID, Azure endpoint URL, or
SDP content.

#### `POST /api/voice/admit`

Requests admission for a new Voice Session for the authenticated Participant and current Channel
Conversation.

Request:
```json
{ "sdpOffer": "<WebRTC SDP offer string>" }
```

Response — admitted (`200 OK`):
```json
{ "admitted": true, "voiceSessionId": "<guid>", "sdpAnswer": "<SDP answer string>" }
```

Response — denied (`200 OK`):
```json
{ "admitted": false, "denialReason": "VoiceDisabled|AlreadyActive|GlobalCapReached" }
```

`denialReason` values: `VoiceDisabled` — voice is not enabled in configuration; `AlreadyActive` —
this Participant already holds an active Voice Session; `GlobalCapReached` — the server-wide
concurrent session ceiling is full. The internal control session identifier used to manage the
provider WebSocket is never returned.

#### `POST /api/voice/heartbeat`

Renews the idle deadline for a Voice Session owned by the authenticated Participant.

Request:
```json
{ "voiceSessionId": "<guid>" }
```

Response (`200 OK`):
```json
{
  "renewed": true,
  "lifecycleState": "active",
  "remainingSeconds": 298,
  "forcedCloseReason": null
}
```

`lifecycleState` semantics:

| Value | `renewed` | Meaning |
|---|---|---|
| `active` | `true` | Session healthy; idle deadline extended. |
| `warning_due` | `true` | Session healthy; warning threshold crossed. `remainingSeconds` until hard expiry. |
| `expired` | `false` | Hard expiry passed; session closed. `forcedCloseReason: "expired"`. |
| `idle` | `false` | Idle timeout elapsed; session closed. `forcedCloseReason: "idle"`. |

A `404` response means the session does not exist or does not belong to the authenticated
Participant — both cases are indistinguishable. The browser client maps `404` to
`lifecycleState: "not_found"`.

#### `POST /api/voice/release`

Terminates a Voice Session owned by the authenticated Participant. Idempotent: an already-ended
session owned by the authenticated Participant returns `200 OK`.

Request:
```json
{ "voiceSessionId": "<guid>" }
```

A session that does not exist or belongs to a different Participant returns `404` without
disclosing which case applies.

#### Turn submission — optional `voiceSessionId`

`POST /api/turns` accepts an optional `voiceSessionId` string in the request body. When present,
the server performs a single authoritative lookup at submission time: if the session is `Active`,
belongs to the current Participant, and belongs to the current Channel Conversation, the Turn is
accepted with Voice Input Modality. Any other outcome — invalid format, session not found,
expired, idle, wrong Participant, wrong Channel Conversation — silently falls back to Text with
ordinary web capabilities. The client body never sets `InputModality` or channel capabilities
directly.

### Configuration

All settings are under the `Voice` configuration section. These are **capacity and lifetime
controls** — they govern how many concurrent sessions are permitted and how long each session may
run. They are not monetary budget, spend, or quota controls.

| Setting | Default | Description |
|---|---|---|
| `Voice:Enabled` | `false` | Enables voice. All other settings are ignored when `false`. |
| `Voice:Endpoint` | *(required when enabled)* | Absolute WSS URI of the Azure Voice Live WebRTC endpoint. Host must end with `.services.ai.azure.com` (primary) or `.cognitiveservices.azure.com` (legacy). |
| `Voice:Model` | *(required when enabled)* | Azure AI model deployment name. |
| `Voice:VoiceName` | `en-US-Ava:DragonHDLatestNeural` | Voice synthesis voice name. |
| `Voice:GlobalActiveCap` | `5` | Maximum concurrent active Voice Sessions across all Participants (capacity limit). |
| `Voice:MaxSessionDuration` | `00:30:00` | Maximum session lifetime after admission. Immutable once computed. |
| `Voice:SessionWarningThreshold` | `00:25:00` | Warning issued at `admission + threshold`; must be greater than zero and strictly less than `MaxSessionDuration`. |
| `Voice:IdleTimeout` | `00:01:00` | Inactivity duration before a session is closed automatically. |
| `Voice:HeartbeatInterval` | `00:00:30` | Browser heartbeat interval; must be less than `IdleTimeout`. |

**Authentication:** voice reuses the same Entra `TokenCredential` registered for the Microsoft
Graph integration — either `ClientSecretCredential` (with `Authentication:Entra:TenantId`,
`ClientId`, and `ClientSecret`) or `DefaultAzureCredential` (when
`Authentication:Entra:UseManagedIdentityForGraph=true`). The token scope is
`https://ai.azure.com/.default`. API-key authentication is not supported. No token, credential,
or Azure URL is exposed in responses, errors, or JavaScript assets.

#### Example configuration

`appsettings.json`:
```json
{
  "Voice": {
    "Enabled": true,
    "Endpoint": "wss://<your-resource>.services.ai.azure.com/openai/deployments/<deployment>",
    "Model": "<your-deployment-name>"
  }
}
```

Equivalent environment variables for a container:
```bash
docker run -p 8080:8080 \
  -e ConnectionStrings__MultiChannelAgent="<connection string>" \
  -e Authentication__Provider="Entra" \
  -e Authentication__Entra__TenantId="<tenant id>" \
  -e Authentication__Entra__ClientId="<client id>" \
  -e Authentication__Entra__ClientSecret="<client secret>" \
  -e Voice__Enabled="true" \
  -e Voice__Endpoint="wss://<your-resource>.services.ai.azure.com/openai/deployments/<deployment>" \
  -e Voice__Model="<your-deployment-name>" \
  multi-channel-agent
```

No Docker Compose file is included; use environment variable syntax as shown above.

### Operational lifecycle

1. **Reserve** — admission atomically inserts a `Negotiating` session row, enforcing one active
   slot per Participant and checking the global cap.
2. **Negotiate** — the backend exchanges SDP with the Azure Voice Live WebSocket outside any SQL
   transaction.
3. **Activate** — on successful negotiation the session moves to `Active`; deadlines computed at
   admission are immutable and are not affected by later configuration changes.
4. **Active** — the browser sends heartbeats to extend the idle deadline up to `ExpiresAt`.
5. **End** — via Participant release, background cleanup, or a heartbeat returning
   `expired`/`idle`.

A failed negotiation releases its slot. Sessions whose owning backend instance has restarted
(identified by the per-process `ownerInstanceId`) are reclaimed by the background cleanup worker.

**SQL Server locking:** the slot-count query uses `UPDLOCK`/`HOLDLOCK` hints, ensuring concurrent
admissions serialize correctly. This is verified against a real SQL Server container in Docker CI.
SQLite (used in fast local tests) provides best-effort concurrency via shared-cache but does not
replicate SQL Server row-level locking; full concurrency correctness requires SQL Server.

The `VoiceSessions` table is added by migration `AddVoiceSessions`. The `InputModality` column
on `InboxEntries` is added by migration `AddInputModality`, with all existing rows defaulting to
`Text`.

### Failure and fallback

All failures return the Participant to text interaction within the same Channel Conversation.
Active Inventory, Inventory access, and all prior Outcomes are unaffected.

| Failure | Behaviour |
|---|---|
| Admission denied or network error | Error shown inline; browser returns to `idle`. |
| Microphone access denied | Error shown; browser returns to `idle`. |
| WebRTC or realtime connection failure | Error shown; browser releases the server session and returns to `idle`. |
| Heartbeat returns `expired` or `idle` | Browser disconnects transport and returns to `idle`. |
| Heartbeat network failure | Session treated as lost; browser releases server session (best-effort) and returns to `idle`. |
| Playback or integrity failure | Error shown; session continues; Outcome text remains visible. |
| Release failure | Server session expiry is authoritative; the cleanup worker reclaims the session. |

An accepted Turn and its recorded Outcome are never replayed regardless of session or transport
failure.

### Testing

Voice tests use deterministic doubles by default and do not require Docker or any Azure dependency:

- **Application-layer** tests (`MultiChannelAgent.Application.Tests/Voice/`) use
  `FakeVoiceLiveGateway` and `InMemoryVoiceSessionStore` for admission, lifecycle, confirmation
  policy, and protocol fixture scenarios.
- **Browser-side** tests (`voiceReducer.test.ts`, `voiceApi.test.ts`, `voiceTransport.test.ts`,
  `voiceIntegration.test.tsx`, `browserVoiceTransport.test.ts`) use a `fakeVoiceTransport` and
  run with `npm run test` (covered by `npm run lint && npm run build`).
- **SQLite concurrency tests** (`SqlVoiceSessionStoreConcurrencyTests`) verify admission races
  using a shared-cache in-memory SQLite database — no Docker required.
- **SQL Server scenario tests** (`VoiceAdmissionSqlScenarioTests`) verify row-level lock
  serialization against a real SQL Server container; they are Docker-gated and follow the same
  `REQUIRE_DOCKER_TESTS` policy as the main integration test suite.
- **`AzureVoiceLiveGatewayTests`** verify URI construction, token injection, protocol behaviour,
  and error paths using a `FakeVoiceWebSocket` — no real Azure Voice Live endpoint is contacted.
  No opt-in real Azure Voice Live contract test has been implemented.

Voice only activates in production when `Voice:Enabled=true` is explicitly set with a valid
`Endpoint` and `Model`. The application starts and operates normally in all other modes without
any voice configuration.

### Security

- All voice endpoints enforce the `ActiveTenantMember` authorization policy and the antiforgery
  header; unauthenticated and CSRF-missing requests are rejected.
- The server derives the Participant and Channel Conversation from the authenticated session
  cookie; the client body cannot set either. The client cannot set Input Modality, channel
  capabilities, or provider session identifiers.
- The provider control session identifier is server-only and never returned in any response or
  error message.
- The Entra access token is acquired per-connection, never stored in the domain model, gateway
  registry, or any response field. Gateway errors are sanitised before surfacing to callers; raw
  provider errors, SDP content, Azure endpoints, and tokens are never forwarded.
- No credential or Azure URL appears in JavaScript assets, browser session state, or network
  responses to the browser.

## Container image

```bash
docker build -t multi-channel-agent .
docker run -p 8080:8080 \
  -e ConnectionStrings__MultiChannelAgent="<connection string>" \
  -e Authentication__Provider="Entra" \
  -e Authentication__Entra__TenantId="<tenant id>" \
  -e Authentication__Entra__ClientId="<client id>" \
  -e Authentication__Entra__ClientSecret="<client secret>" \
  multi-channel-agent
```

The multi-stage `Dockerfile` builds the web client (Node), publishes the backend (.NET SDK), and
combines both into a single ASP.NET Core runtime image that serves the API, health endpoints, and the
built SPA from one origin. The container's default `Authentication:Provider` is `Entra`; it refuses
to start without the three Entra values above configured (`ASPNETCORE_ENVIRONMENT=Production` also
refuses `Authentication__Provider=Test` outright).

## Continuous integration

`.github/workflows/ci.yml` runs on every push/PR:

- **backend**: restore, build (`TreatWarningsAsErrors`), EF Core migration script validation
  (`dotnet ef migrations script --idempotent`), and `dotnet test` — which covers the domain/application
  unit tests, the architecture tests, and the SQL-backed application-boundary scenario (Docker is
  preinstalled on GitHub-hosted runners).
- **frontend**: `npm ci`, lint, and build for the web client.
