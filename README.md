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
