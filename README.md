# Multi-Channel Agent

A single deployable ASP.NET Core + React application that accepts a normalized Turn, persists it
through production-style SQL migrations and hosted workers, and exposes its recorded terminal
Outcome through one application boundary ([issue #27](https://github.com/JoranBergfeld/multi-channel-agent/issues/27)).
It also implements the signed-in web BFF session contract and the first slice of the Inventory
domain - explicit Inventory creation, listing, and selection
([issue #28](https://github.com/JoranBergfeld/multi-channel-agent/issues/28)). See the parent spec
(issue #26) and the domain vocabulary in `CONTEXT.md`.

## Solution layout

```text
src/
  MultiChannelAgent.Domain/          Pure domain model: InboundTurn, Outcome, Delivery, and the
                                      Inventory aggregate (Participant, Inventory, Membership, Unit,
                                      ActiveInventorySelection). No dependencies.
  MultiChannelAgent.Application/     Application boundary: TurnAcceptanceService, TurnProcessingCoordinator,
                                      DeliveryDispatchCoordinator, TurnOutcomeReader, the scripted model
                                      boundary, the Inventory bootstrap/creation/listing/selection
                                      services, and the repository/lease/model/delivery abstractions
                                      Infrastructure implements. Depends only on Domain.
  MultiChannelAgent.Infrastructure/  EF Core SQL Server DbContext, entity configurations, production
                                      migrations, and the SQL-backed repositories/lease coordinator.
                                      Depends on Domain and Application.
  MultiChannelAgent.Host/            ASP.NET Core: HTTP endpoints, health checks, hosted workers,
                                      the Entra/Test authentication contract, CSRF-protected session
                                      and Inventory endpoints, and the published React/Vite client
                                      (wwwroot). Depends on all of the above.
  web/                               React + TypeScript + Vite client: signed-in onboarding, Inventory
                                      create/list/select, and the Turn tracer.
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
- `POST /api/turns` — submit a normalized synthetic `InboundTurn`:
  `{ "nativeMessageId": "...", "channelConversationId": "...", "contentText": "...", "locale": "en-US", "traceId": "..." }`.
  Returns `202 Accepted` with `{ "turnId": "...", "alreadyAccepted": false }`. Resubmitting the same
  `nativeMessageId` returns the original `turnId` with `alreadyAccepted: true` instead of duplicating
  acceptance or reprocessing.
- `GET /api/turns/{turnId}/outcome` — the recorded terminal Outcome and its Deliveries once hosted
  processing completes (`404` until then).

The scripted model boundary (`ScriptedModelBoundary`) echoes ordinary content back as one requested
Delivery; a Turn whose content is exactly `trigger-scripted-failure` produces a failed Outcome with no
Delivery, so both terminal paths are reproducible without any external model dependency.

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
