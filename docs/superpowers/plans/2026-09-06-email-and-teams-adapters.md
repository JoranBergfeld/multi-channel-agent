# Email and Teams Personal-Chat Adapters Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Complete issue #39 (add the authenticated internal email conversation adapter) and issue #37 (add the Teams personal-chat adapter) so both channels produce `InboundTurn`s through the existing channel-neutral Turn pipeline and deliver replies natively, with zero changes to `TurnAcceptanceService`, `TurnProcessingCoordinator`, `TurnExecutionContextFactory`, `ConfirmationProposalLifecycle`, or `IToolDispatcher`.

**Architecture:** Both adapters are thin translators: native channel event → `InboundTurnDraft` → `IInboxStore.AcceptAsync` (existing, unmodified). The channel-neutral core already does everything downstream of acceptance; this plan only adds ingress (webhook/activity endpoints), sender resolution, and egress (per-channel reply senders).

A shared groundwork task precedes both parts. First, `ChannelPrincipal` gains an `ExchangeMailbox` factory (mirroring the existing `EntraUser` factory) since email's evidence is a verified mailbox address, not an Entra object id. Second, `Delivery.Channel` is not usable for routing: it is always the hardcoded literal `"conversation"` (`StockToolDispatcher.ResponseChannel`), set by tool dispatch logic that has no notion of the channel a Turn arrived on. A real per-channel sender must instead resolve the *origin Turn's* `Channel` via `IInboxStore.FindByTurnIdAsync(delivery.TurnId)` and route on that. A new `ChannelRoutingDeliverySender : IDeliverySender` does this resolution and dispatches to a channel-keyed `IDeliverySender` registered via .NET keyed DI (`AddKeyedSingleton<IDeliverySender, T>("email"|"teams"|"web")`), replacing the single unconditional `AddScoped<IDeliverySender, LoggingDeliverySender>()` registration. `LoggingDeliverySender` remains registered under the `"web"` key (web's real send path is out of scope here and continues to log) and as the fallback for any unrecognized channel.

**Part A — Email (#39):** One dedicated Exchange Online mailbox, Microsoft Graph "basic" (non-encrypted) change notifications via `POST /api/channels/email/notifications`. Ticket #40 (subscription creation/renewal/self-healing) is **explicitly out of scope**: this plan assumes a subscription already exists and only implements the webhook receiver, message fetch, sender resolution, Turn shaping, and reply. The webhook validates Graph's `?validationToken=` handshake and, per notification, a configured `clientState` shared secret; mismatches are silently rejected with `202 Accepted` (never `4xx`, to avoid Graph retry storms and to avoid confirming validity to a prober). On a valid notification the adapter fetches the full message via `GET /users/{mailbox}/messages/{id}` with `Prefer: IdType="ImmutableId"` (stable ids) and `$select=uniqueBody,from,sender,conversationId,receivedDateTime`. Graph's `uniqueBody` cleanly separates the new reply text (`ContentProvenance.Direct`) from quoted/forwarded history (`ContentProvenance.Quoted`) — no heuristic parsing needed. The sender's address resolves to a `ParticipantId` via the existing `ITenantMemberDirectory.ResolveAsync` (already excludes guests/disabled/non-member accounts); unresolved senders are dropped, no Turn is accepted. Threading uses Graph's `conversationId` as `ChannelConversationId`; replies use the single-call `POST /users/{mailbox}/messages/{id}/reply` convenience action. All Graph access goes through a typed `HttpClient` + `Azure.Identity` `TokenCredential`, following the existing `GraphTenantMemberDirectory`/`GraphCredentialFactory` convention — **no Microsoft.Graph SDK package** is introduced, matching current repository practice.

**Part B — Teams personal chat (#37):** Personal (1:1) chat only; ticket #38 (group-chat disclosure rules) is **explicitly out of scope** — the activity handler no-ops on any non-`personal` `conversationType`. Uses the M365 Agents SDK (`Microsoft.Agents.Hosting.AspNetCore`, `Microsoft.Agents.Authentication.Msal`, `Microsoft.Agents.Core`) mounted at `POST /api/messages`, whose bundled JWT middleware validates the inbound Azure Bot Service token — no reuse of the existing `ActiveTenantMember` cookie policy. Identity reuses the existing `ChannelPrincipal.EntraUser` (its doc comment already anticipates "Teams later"): `Subject` from `Activity.From.AadObjectId` (platform-set, trusted), `TenantId` from the activity's conversation tenant. `ChannelConversationId` is `Activity.Conversation.Id`; `NativeMessageId` is `Activity.Id`. Turn processing is asynchronous (background `TurnProcessingWorker` / `DeliveryDispatchWorker`), so the reply cannot be sent synchronously inside the inbound HTTP request: at ingress the adapter persists a serialized conversation reference (service URL, bot/user/conversation/tenant ids) keyed by `ChannelConversationId` via a new `ITeamsConversationReferenceStore` (+ `SqlTeamsConversationReferenceStore`, new EF table/migration), then `TeamsDeliverySender` later proactively resumes that conversation and sends the reply via the SDK's continue-conversation mechanism. Scope is Text capability only for this plan; Adaptive Card rendering is deferred.

**No live Microsoft 365 tenant is available** (per ticket #19's resolution): every test in this plan is fixture/mock-driven — recorded/constructed Graph JSON payloads for email, constructed `Activity` POCOs plus a test double adapter for Teams. No live-tenant integration test is added.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, EF Core 10 (SQL Server prod / SQLite tests), `Azure.Identity`, xUnit 2.9, `Xunit.SkippableFact`, `Microsoft.Extensions.TimeProvider.Testing`, Testcontainers `MsSql`, Microsoft Agents SDK (`Microsoft.Agents.Hosting.AspNetCore`, `Microsoft.Agents.Authentication.Msal`, `Microsoft.Agents.Core`).

---

## File Map

Every file created or modified, grouped by responsibility. Tasks reference these exact paths.

### Shared groundwork

| Action | Path | Responsibility |
|--------|------|-----------------|
| Modify | `src/MultiChannelAgent.Domain/Turns/ChannelPrincipal.cs` | Add `ExchangeMailbox(string subject, string? tenantId)` factory |
| Create | `src/MultiChannelAgent.Infrastructure/Turns/ChannelRoutingDeliverySender.cs` | Resolves origin Turn's `Channel` via `IInboxStore.FindByTurnIdAsync`, dispatches to keyed `IDeliverySender` |
| Modify | `src/MultiChannelAgent.Infrastructure/ServiceCollectionExtensions.cs` | Replace unconditional `IDeliverySender` registration with keyed registrations + `ChannelRoutingDeliverySender` as the resolved `IDeliverySender` |
| Create | `tests/MultiChannelAgent.IntegrationTests/Turns/ChannelRoutingDeliverySenderTests.cs` | Routes by origin-Turn channel; falls back safely when Turn/channel unknown |

### Part A — Email (#39)

| Action | Path | Responsibility |
|--------|------|-----------------|
| Create | `src/MultiChannelAgent.Domain/Turns/EmailChannel.cs` | `public const string Name = "email"` + capability constant |
| Create | `src/MultiChannelAgent.Application/Channels/Email/GraphInboundMessage.cs` | DTO: Id, ConversationId, SenderAddress, DirectText, QuotedText?, ReceivedAt |
| Create | `src/MultiChannelAgent.Application/Channels/Email/IGraphMailboxClient.cs` | `GetMessageAsync`, `SendReplyAsync` contract |
| Create | `src/MultiChannelAgent.Infrastructure/Channels/Email/GraphMailboxClient.cs` | Typed-`HttpClient` + `TokenCredential` Graph implementation (no Graph SDK) |
| Create | `src/MultiChannelAgent.Application/Channels/Email/EmailSenderResolutionService.cs` | Address → `ParticipantId` via `ITenantMemberDirectory` |
| Create | `src/MultiChannelAgent.Application/Channels/Email/EmailInboundProcessingService.cs` | Notification → fetched message → `InboundTurnDraft` → `AcceptAsync` |
| Create | `src/MultiChannelAgent.Host/Endpoints/Channels/EmailWebhookEndpoints.cs` | Anonymous `GET/POST /api/channels/email/notifications`: validation handshake, `clientState` check, dispatch |
| Create | `src/MultiChannelAgent.Infrastructure/Channels/Email/GraphEmailDeliverySender.cs` | `IDeliverySender` keyed `"email"`; calls `IGraphMailboxClient.SendReplyAsync` |
| Modify | `src/MultiChannelAgent.Infrastructure/ServiceCollectionExtensions.cs` | Register `IGraphMailboxClient`, `EmailSenderResolutionService`, `EmailInboundProcessingService`, keyed `"email"` sender |
| Modify | `src/MultiChannelAgent.Host/Program.cs` | Map `EmailWebhookEndpoints` |
| Create | `tests/MultiChannelAgent.Application.Tests/Channels/Email/EmailInboundProcessingServiceTests.cs` | Fixture Graph payloads → correct `InboundTurnDraft` shaping (direct/quoted split, sender resolution failure path) |
| Create | `tests/MultiChannelAgent.IntegrationTests/Channels/EmailWebhookEndpointsTests.cs` | Handshake echo, `clientState` mismatch → 202 no-op, valid notification → accepted Turn end-to-end via `SqliteWebApplicationFactory` |

### Part B — Teams personal chat (#37)

| Action | Path | Responsibility |
|--------|------|-----------------|
| Modify | `src/MultiChannelAgent.Host/MultiChannelAgent.Host.csproj` | Add `Microsoft.Agents.Hosting.AspNetCore`, `Microsoft.Agents.Authentication.Msal`, `Microsoft.Agents.Core` package references |
| Create | `src/MultiChannelAgent.Domain/Turns/TeamsChannel.cs` | `public const string Name = "teams"` + capability constant |
| Create | `src/MultiChannelAgent.Application/Channels/Teams/ITeamsConversationReferenceStore.cs` | Save/find serialized conversation reference by `ChannelConversationId` |
| Create | `src/MultiChannelAgent.Infrastructure/Channels/Teams/SqlTeamsConversationReferenceStore.cs` | EF Core implementation |
| Create | `src/MultiChannelAgent.Infrastructure/Persistence/Entities/TeamsConversationReferenceEntity.cs` | EF entity |
| Create | `src/MultiChannelAgent.Infrastructure/Persistence/Migrations/<timestamp>_AddTeamsConversationReferences.cs` | EF migration |
| Modify | `src/MultiChannelAgent.Infrastructure/Persistence/MultiChannelAgentDbContext.cs` | Register `DbSet<TeamsConversationReferenceEntity>` + configuration |
| Create | `src/MultiChannelAgent.Host/Channels/Teams/InventoryAgentTeamsHandler.cs` | Activity handler: personal-chat guard, principal + draft shaping, `AcceptAsync` call, reference persistence |
| Create | `src/MultiChannelAgent.Host/Endpoints/Channels/TeamsActivityEndpoints.cs` | Maps `POST /api/messages` to the Agents SDK adapter/handler |
| Create | `src/MultiChannelAgent.Infrastructure/Channels/Teams/TeamsDeliverySender.cs` | `IDeliverySender` keyed `"teams"`; loads stored reference, proactively continues conversation, sends reply |
| Modify | `src/MultiChannelAgent.Infrastructure/ServiceCollectionExtensions.cs` | Register `ITeamsConversationReferenceStore`, Agents SDK adapter/auth services, keyed `"teams"` sender |
| Modify | `src/MultiChannelAgent.Host/Program.cs` | Map `TeamsActivityEndpoints`, wire Agents SDK middleware |
| Create | `tests/MultiChannelAgent.IntegrationTests/Channels/InventoryAgentTeamsHandlerTests.cs` | Personal-chat activity → accepted Turn; non-personal `conversationType` → no-op; missing `AadObjectId` → rejected |
| Create | `tests/MultiChannelAgent.IntegrationTests/Channels/Teams/TeamsDeliverySenderTests.cs` | Stored reference → send invoked with correct text; missing reference → safe no-op/false |

---

## Task 0 — Shared groundwork

- [ ] **0.1 Add `ChannelPrincipal.ExchangeMailbox` factory**

  In `src/MultiChannelAgent.Domain/Turns/ChannelPrincipal.cs`, add a factory mirroring `EntraUser`:

  ```csharp
  /// <summary>An authenticated internal Exchange mailbox address, resolved through the verified directory.</summary>
  public static ChannelPrincipal ExchangeMailbox(string subject, string? tenantId) => new()
  {
      Kind = ChannelPrincipalKind.ExchangeMailbox,
      Subject = RequireNonBlank(subject, nameof(subject)),
      TenantId = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId.Trim(),
  };
  ```

  Add a unit test in the existing `ChannelPrincipal` test file asserting blank subject throws and a valid subject/tenant round-trips.

- [ ] **0.2 Add `ChannelRoutingDeliverySender`**

  Create `src/MultiChannelAgent.Infrastructure/Turns/ChannelRoutingDeliverySender.cs`:

  ```csharp
  namespace MultiChannelAgent.Infrastructure.Turns;

  /// <summary>
  /// Routes a <see cref="Delivery"/> to the sender for the channel the originating Turn arrived on.
  /// <see cref="Delivery.Channel"/> is never this channel - it is the tool dispatcher's fixed response
  /// label - so routing always resolves the origin Turn via <see cref="IInboxStore.FindByTurnIdAsync"/>
  /// and dispatches on its <see cref="InboundTurn.Channel"/> instead.
  /// </summary>
  public sealed class ChannelRoutingDeliverySender(
      IInboxStore inboxStore,
      IServiceProvider serviceProvider,
      ILogger<ChannelRoutingDeliverySender> logger) : IDeliverySender
  {
      public async Task<bool> TrySendAsync(Delivery delivery, CancellationToken cancellationToken)
      {
          var originTurn = await inboxStore.FindByTurnIdAsync(delivery.TurnId, cancellationToken);
          if (originTurn?.Channel is not { } channel)
          {
              logger.LogWarning("No origin Turn/channel found for delivery {TurnId}; dropping.", delivery.TurnId);
              return false;
          }

          var sender = serviceProvider.GetKeyedService<IDeliverySender>(channel);
          if (sender is null)
          {
              logger.LogWarning("No delivery sender registered for channel {Channel}; dropping.", channel);
              return false;
          }

          return await sender.TrySendAsync(delivery, cancellationToken);
      }
  }
  ```

  Note the exact `Delivery`/`InboundTurn` member names (`TurnId`, `Channel`) against the real types before finalizing — verify against `src/MultiChannelAgent.Domain/Turns/Delivery.cs` and `InboundTurn.cs` since this snippet is written from the researched shapes.

- [ ] **0.3 Rewire DI for keyed delivery senders**

  In `src/MultiChannelAgent.Infrastructure/ServiceCollectionExtensions.cs`, replace the line:

  ```csharp
  services.AddScoped<IDeliverySender, LoggingDeliverySender>();
  ```

  with:

  ```csharp
  services.AddKeyedScoped<IDeliverySender, LoggingDeliverySender>("web");
  services.AddScoped<IDeliverySender, ChannelRoutingDeliverySender>();
  ```

  (Part A and Part B tasks below each add their own `AddKeyedScoped<IDeliverySender, ...>("email"|"teams")` line beside this.)

- [ ] **0.4 Tests for `ChannelRoutingDeliverySender`**

  `tests/MultiChannelAgent.IntegrationTests/Turns/ChannelRoutingDeliverySenderTests.cs`: fake `IInboxStore` returning a Turn with `Channel = "web"` routes to the keyed `"web"` fake sender; a Turn with no match / null channel returns `false` without throwing; an unregistered channel key returns `false`.

## Part A — Email adapter (#39)

- [ ] **A.1 `EmailChannel` constant**

  `src/MultiChannelAgent.Domain/Turns/EmailChannel.cs`:

  ```csharp
  namespace MultiChannelAgent.Domain.Turns;

  /// <summary>Channel-name and capability constants for the internal email adapter.</summary>
  public static class EmailChannel
  {
      public const string Name = "email";
  }
  ```

- [ ] **A.2 `GraphInboundMessage` DTO and `IGraphMailboxClient`**

  `src/MultiChannelAgent.Application/Channels/Email/GraphInboundMessage.cs`:

  ```csharp
  namespace MultiChannelAgent.Application.Channels.Email;

  /// <summary>
  /// One fetched Graph message, already split into direct and quoted/forwarded content via Graph's
  /// own <c>uniqueBody</c> — never heuristically parsed — so provenance stays exact.
  /// </summary>
  public sealed record GraphInboundMessage(
      string ImmutableId,
      string ConversationId,
      string SenderAddress,
      string DirectText,
      string? QuotedText,
      DateTimeOffset ReceivedAt);
  ```

  `src/MultiChannelAgent.Application/Channels/Email/IGraphMailboxClient.cs`:

  ```csharp
  namespace MultiChannelAgent.Application.Channels.Email;

  /// <summary>Graph access for the single dedicated inbound mailbox, isolated so tests never call Graph.</summary>
  public interface IGraphMailboxClient
  {
      Task<GraphInboundMessage?> GetMessageAsync(string messageId, CancellationToken cancellationToken);

      Task SendReplyAsync(string messageId, string replyText, CancellationToken cancellationToken);
  }
  ```

- [ ] **A.3 `GraphMailboxClient` implementation**

  `src/MultiChannelAgent.Infrastructure/Channels/Email/GraphMailboxClient.cs`: typed `HttpClient` (constructor-injected, `BaseAddress` set in DI registration to `https://graph.microsoft.com/v1.0/`) + `TokenCredential` (reuse `GraphCredentialFactory.Create`), following `GraphTenantMemberDirectory`'s exact pattern for acquiring a bearer token via `credential.GetTokenAsync(new TokenRequestContext(["https://graph.microsoft.com/.default"]), cancellationToken)` and attaching `Authorization: Bearer {token}`.

  `GetMessageAsync` calls `GET /users/{mailbox}/messages/{messageId}?$select=uniqueBody,from,sender,conversationId,receivedDateTime` with header `Prefer: IdType="ImmutableId"`; parses `uniqueBody.content` (Graph returns unique-body HTML/text with the quoted portion Graph itself strips — when a quoted portion is not separately exposed by `uniqueBody`, treat the entire body as `DirectText` with `QuotedText = null`, since `uniqueBody` already excludes quoted history by Graph's own definition). Maps Graph's mailbox-scoped immutable `id` as `ImmutableId`.

  `SendReplyAsync` calls `POST /users/{mailbox}/messages/{messageId}/reply` with body `{"comment": replyText}`.

  Mailbox address is read from configuration (`EmailChannel:MailboxAddress`), injected via `IOptions<EmailChannelOptions>` (new small options record created alongside this file).

- [ ] **A.4 `EmailSenderResolutionService`**

  `src/MultiChannelAgent.Application/Channels/Email/EmailSenderResolutionService.cs`: wraps `ITenantMemberDirectory.ResolveAsync(TenantMemberIdentifier.Parse(senderAddress), cancellationToken)`; returns `ParticipantId?` — `null` when the directory returns no member (external senders, distribution groups, disabled accounts are already excluded by the directory's existing resolution rules). No new trust logic is introduced; this class only adapts the call site.

- [ ] **A.5 `EmailInboundProcessingService`**

  `src/MultiChannelAgent.Application/Channels/Email/EmailInboundProcessingService.cs`: given a Graph notification's `resourceData.id`, calls `IGraphMailboxClient.GetMessageAsync`, then `EmailSenderResolutionService` to resolve the Participant (returns a discriminated outcome such as `EmailProcessingOutcome.Accepted(InboundTurn)` / `.SenderUnresolved` / `.MessageNotFound` — no Turn accepted on non-`Accepted` outcomes). On success, builds an `InboundTurnDraft`:

  ```csharp
  new InboundTurnDraft
  {
      NativeMessageId = message.ImmutableId,
      ParticipantId = participantId,
      ChannelConversationId = message.ConversationId,
      Channel = EmailChannel.Name,
      Principal = ChannelPrincipal.ExchangeMailbox(message.SenderAddress, tenantId: null),
      Capabilities = ChannelCapabilities.Text,
      ContentParts =
      [
          new TurnContentPart(message.DirectText, ContentProvenance.Direct),
          .. message.QuotedText is { } quoted
              ? new[] { new TurnContentPart(quoted, ContentProvenance.Quoted) }
              : [],
      ],
      ReceivedAt = message.ReceivedAt,
  }
  ```

  then calls `IInboxStore.AcceptAsync(draft, cancellationToken)` (existing, unmodified) and returns the result. Verify exact `TurnContentPart`/`ChannelCapabilities`/`ContentProvenance` member names against `src/MultiChannelAgent.Domain/Turns/TurnContentPart.cs` and `ChannelCapabilities.cs` before finalizing, since this is written from researched shapes.

- [ ] **A.6 `EmailWebhookEndpoints`**

  `src/MultiChannelAgent.Host/Endpoints/Channels/EmailWebhookEndpoints.cs`, anonymous-auth minimal API mapped at `POST /api/channels/email/notifications`:

  - If the query string carries `validationToken` (Graph's subscription-creation handshake — this endpoint only needs to answer it; subscription creation itself is ticket #40's scope), return `200 OK` with `Content-Type: text/plain` and the token echoed verbatim as the body.
  - Otherwise, parse the notification envelope (`{"value": [{"clientState": ..., "resourceData": {"id": ...}}, ...]}`). For each entry: if `clientState` does not match the configured secret (constant-time comparison), skip that entry silently. For matching entries, call `EmailInboundProcessingService` fire-and-forget-safe (awaited, but any single entry's exception is caught and logged, never allowed to fail the whole batch or surface Graph-visible detail).
  - Always return `202 Accepted` for a syntactically valid notification payload, regardless of per-entry outcome, so Graph never retries because of an application-level decision (message already accepted idempotently, sender unresolved, etc.) — only a genuinely malformed body may return `400`.

- [ ] **A.7 `GraphEmailDeliverySender`**

  `src/MultiChannelAgent.Infrastructure/Channels/Email/GraphEmailDeliverySender.cs`: `IDeliverySender` implementation; extracts the delivery's textual payload (verify the deliverable payload shape/property against `Delivery.cs`) and calls `IGraphMailboxClient.SendReplyAsync(originalMessageId, payloadText, cancellationToken)`. The original Graph message id needed for the reply-to relationship is the origin Turn's `NativeMessageId` (available via the same `IInboxStore.FindByTurnIdAsync` call `ChannelRoutingDeliverySender` already made — pass the resolved `InboundTurn` through, or re-resolve here; prefer re-resolving here to keep `ChannelRoutingDeliverySender` free of channel-specific data shuttling). Returns `false` (not throwing) on any Graph error, matching `IDeliverySender`'s documented at-least-once-retry contract.

- [ ] **A.8 DI + Program.cs wiring**

  In `ServiceCollectionExtensions.cs`: register `IGraphMailboxClient` via `AddHttpClient<GraphMailboxClient>(...)` (same convention as `GraphTenantMemberDirectory`, line ~106), `EmailSenderResolutionService`, `EmailInboundProcessingService`, and `services.AddKeyedScoped<IDeliverySender, GraphEmailDeliverySender>("email");`. In `Program.cs`, map `EmailWebhookEndpoints`.

- [ ] **A.9 Tests**

  - `EmailInboundProcessingServiceTests`: fixture Graph JSON → correct draft shaping (direct/quoted split when `QuotedText` present and absent); sender resolution failure → no `AcceptAsync` call; duplicate `ImmutableId` (simulate `WasAlreadyAccepted = true`) handled without error.
  - `EmailWebhookEndpointsTests` (via `SqliteWebApplicationFactory`): validation handshake echoes token; wrong `clientState` → `202` with no Turn created; valid notification with a fixture message id (backed by a fake `IGraphMailboxClient`) → `202` and a Turn is durably accepted, verifiable via the store.

## Part B — Teams personal-chat adapter (#37)

- [ ] **B.1 Add Agents SDK package references**

  Add to `src/MultiChannelAgent.Host/MultiChannelAgent.Host.csproj`: `Microsoft.Agents.Hosting.AspNetCore`, `Microsoft.Agents.Authentication.Msal`, `Microsoft.Agents.Core` (latest stable versions at implementation time — pin exact versions when adding, and verify the adapter/handler API surface against the installed version's actual types before finalizing code in B.5/B.6, since this SDK's public API has changed across recent releases).

- [ ] **B.2 `TeamsChannel` constant**

  `src/MultiChannelAgent.Domain/Turns/TeamsChannel.cs`, mirroring `EmailChannel.cs`: `public const string Name = "teams";`.

- [ ] **B.3 `ITeamsConversationReferenceStore` + SQL implementation**

  `src/MultiChannelAgent.Application/Channels/Teams/ITeamsConversationReferenceStore.cs`:

  ```csharp
  namespace MultiChannelAgent.Application.Channels.Teams;

  /// <summary>
  /// Durable store for the serialized conversation reference Teams requires to proactively resume a
  /// 1:1 chat later, since reply delivery happens on a background worker, never inside the inbound
  /// activity request.
  /// </summary>
  public interface ITeamsConversationReferenceStore
  {
      Task SaveAsync(string channelConversationId, string serializedReference, CancellationToken cancellationToken);

      Task<string?> FindAsync(string channelConversationId, CancellationToken cancellationToken);
  }
  ```

  `TeamsConversationReferenceEntity`: `ChannelConversationId` (PK, string), `SerializedReference` (string, the SDK's own JSON-serializable conversation reference type), `UpdatedAt` (DateTimeOffset, `TimeProvider`-sourced). `SqlTeamsConversationReferenceStore` upserts by `ChannelConversationId`. Add the EF migration following the existing migration-naming convention in `src/MultiChannelAgent.Infrastructure/Persistence/Migrations/`.

- [ ] **B.4 `InventoryAgentTeamsHandler`**

  `src/MultiChannelAgent.Host/Channels/Teams/InventoryAgentTeamsHandler.cs`: extends the Agents SDK's base activity handler. On `OnMessageActivityAsync(ITurnContext turnContext, CancellationToken cancellationToken)`:

  - If `turnContext.Activity.Conversation.ConversationType is not ("personal" or null)`, return without processing (group/channel scope is ticket #38).
  - If `turnContext.Activity.From.AadObjectId` is null/blank, return without processing (no trusted identity — never fall back to a display name).
  - Persist the current conversation reference via `ITeamsConversationReferenceStore.SaveAsync(turnContext.Activity.Conversation.Id, <SDK-serialized reference>, cancellationToken)` — do this before acceptance so a reply path always exists even if a later step fails.
  - Build the draft:

    ```csharp
    new InboundTurnDraft
    {
        NativeMessageId = turnContext.Activity.Id,
        ParticipantId = participantId, // resolved the same way existing web auth maps oid -> ParticipantId
        ChannelConversationId = turnContext.Activity.Conversation.Id,
        Channel = TeamsChannel.Name,
        Principal = ChannelPrincipal.EntraUser(turnContext.Activity.From.AadObjectId, tenantId),
        Capabilities = ChannelCapabilities.Text,
        ContentParts = [new TurnContentPart(turnContext.Activity.Text, ContentProvenance.Direct)],
        ReceivedAt = timeProvider.GetUtcNow(),
    }
    ```

  - Call `IInboxStore.AcceptAsync(draft, cancellationToken)`. No synchronous reply is sent here — delivery happens later via `TeamsDeliverySender`.
  - Verify the exact participant-resolution mechanism for an Entra `oid` against how existing web auth performs it (`ParticipantClaims.cs`) and reuse the same lookup rather than inventing a second one.

- [ ] **B.5 `TeamsActivityEndpoints`**

  `src/MultiChannelAgent.Host/Endpoints/Channels/TeamsActivityEndpoints.cs`: maps `POST /api/messages` (anonymous ASP.NET Core auth; the Agents SDK's own middleware validates the Azure Bot Service JWT) to the SDK's standard adapter-processes-request-then-invokes-handler pattern. Exact call shape depends on the installed SDK version (verify against package docs/samples at implementation time per the B.1 caveat).

- [ ] **B.6 `TeamsDeliverySender`**

  `src/MultiChannelAgent.Infrastructure/Channels/Teams/TeamsDeliverySender.cs`: `IDeliverySender` implementation. Resolves the origin Turn (same double-resolution approach as `GraphEmailDeliverySender`, via `IInboxStore.FindByTurnIdAsync`) to get `ChannelConversationId`, loads the stored reference via `ITeamsConversationReferenceStore.FindAsync`; if none found, logs and returns `false` (no crash — the conversation may have been ended/reference not yet observed). Otherwise deserializes the reference and uses the SDK's continue-conversation mechanism to send the delivery's payload text as a new activity. Returns `false` on any SDK/transport error, matching `IDeliverySender`'s retry contract.

- [ ] **B.7 DI + Program.cs wiring**

  In `ServiceCollectionExtensions.cs`: register `ITeamsConversationReferenceStore`, the Agents SDK's adapter/credential-provider services (per package docs), and `services.AddKeyedScoped<IDeliverySender, TeamsDeliverySender>("teams");`. In `Program.cs`: map `TeamsActivityEndpoints` and any required Agents SDK middleware.

- [ ] **B.8 EF Core migration**

  Add the migration for `TeamsConversationReferenceEntity` via `dotnet ef migrations add AddTeamsConversationReferences` in the Infrastructure project; verify it applies cleanly against both the SQLite test provider and (if Docker is available) the Testcontainers SQL Server suite.

- [ ] **B.9 Tests**

  - `InventoryAgentTeamsHandlerTests`: personal-chat message activity with a valid `AadObjectId` → Turn accepted with correct draft shape and a saved conversation reference; `conversationType = "groupChat"` → handler no-ops, nothing saved/accepted; missing `AadObjectId` → no-op.
  - `TeamsDeliverySenderTests`: stored reference present → continue-conversation invoked with the delivery's text; no stored reference → returns `false`, no exception.

## Final validation

- [ ] **F.1 Full solution build**

  ```
  dotnet build MultiChannelAgent.sln
  ```

- [ ] **F.2 Full test suite**

  ```
  dotnet test MultiChannelAgent.sln
  ```

  All existing tests continue to pass (especially anything currently exercising `LoggingDeliverySender`'s unconditional registration — update any test that assumed the single unkeyed registration) plus every new test added by this plan.

- [ ] **F.3 Acceptance-criteria pass**

  Re-read ticket #39 and ticket #37 acceptance criteria one line at a time against the shipped code and tests; note in the closing PR/commit which line is satisfied by which test.

---

## Self-Review

- **Spec coverage:** Task 0 covers the shared `ChannelPrincipal.ExchangeMailbox` gap and the `Delivery.Channel`-routing gap discovered during research, both required by either ticket's acceptance criteria (native threading/reply, correct principal typing). Part A covers #39's stated ACs: immutable message identities (`Prefer: IdType="ImmutableId"`), direct/quoted provenance (`uniqueBody`), native threading (`conversationId` + `/reply`), sender resolution via the verified directory, no live-tenant test dependency. Part B covers #37's stated ACs: personal-chat scope only (group chat explicitly deferred to #38), trusted Entra identity, proactive reply given async processing, no live-tenant test dependency. Ticket #40 (subscription lifecycle) and #38 (group chat) are deliberately excluded per their own out-of-scope tickets.
- **No placeholders:** All new interfaces/records are given complete member lists; call sites for existing types (`Delivery`, `InboundTurn`, `TurnContentPart`, `ChannelCapabilities`) are flagged inline wherever this plan's snippet was written from research notes rather than a fresh read of the file, with an explicit instruction to verify exact member names before finalizing — this is a verification step, not an unresolved design decision.
- **Type consistency:** `EmailChannel.Name`/`TeamsChannel.Name` are used identically as both the `InboundTurnDraft.Channel` value at ingress and the keyed-DI key at egress, so `ChannelRoutingDeliverySender`'s resolution is guaranteed to find a match for both new channels; `"web"` continues to map to `LoggingDeliverySender` unchanged.
- **Scope-check note:** Email and Teams are independent subsystems (different protocols/SDKs) that would normally warrant separate plans; they are combined here at the user's explicit request, structured so Part A and Part B each remain independently executable once Task 0 is done.

---

## How would you like to proceed?

This plan is ready for execution. Two options:

1. **Subagent-driven development** (`superpowers:subagent-driven-development`) — dispatches Task 0, then Part A and Part B (independent once Task 0 is done, so they can run in parallel), to separate subagents, each executing its tasks end-to-end with review checkpoints.
2. **Inline execution** (`superpowers:executing-plans`) — works through the tasks directly in this session, one at a time, checking off boxes as they complete.

Let me know which you'd prefer, or name a specific task/part to start with.
