# Multi-Channel Agent

A single deployable ASP.NET Core + React application that accepts a normalized Turn, persists it
through production-style SQL migrations and hosted workers, and exposes its recorded terminal
Outcome through one application boundary. This is the foundation tracer built for
[issue #27](https://github.com/JoranBergfeld/multi-channel-agent/issues/27); it deliberately does
not yet implement the Inventory domain (see the parent spec, issue #26, and the domain vocabulary in
`CONTEXT.md`).

## Solution layout

```text
src/
  MultiChannelAgent.Domain/          Pure domain model: InboundTurn, Outcome, Delivery. No dependencies.
  MultiChannelAgent.Application/     Application boundary: TurnAcceptanceService, TurnProcessingCoordinator,
                                      DeliveryDispatchCoordinator, TurnOutcomeReader, the scripted model
                                      boundary, and the repository/lease/model/delivery abstractions
                                      Infrastructure implements. Depends only on Domain.
  MultiChannelAgent.Infrastructure/  EF Core SQL Server DbContext, entity configurations, production
                                      migrations, and the SQL-backed repositories/lease coordinator.
                                      Depends on Domain and Application.
  MultiChannelAgent.Host/            ASP.NET Core: HTTP endpoints, health checks, hosted workers, and the
                                      published React/Vite client (wwwroot). Depends on all of the above.
  web/                               React + TypeScript + Vite client (minimal Turn tracer UI).
tests/
  MultiChannelAgent.Domain.Tests/         Pure unit tests for domain types.
  MultiChannelAgent.Application.Tests/    Application-layer tests against in-memory fakes and the
                                           scripted model boundary/controllable clock.
  MultiChannelAgent.ArchitectureTests/     NetArchTest layering/dependency-direction checks.
  MultiChannelAgent.IntegrationTests/      The SQL-backed application-boundary scenario: an ephemeral
                                           Testcontainers SQL Server, production EF Core migrations,
                                           and a real HTTP round trip through the Host.
```

Dependency direction is enforced both by project references (a reversed reference is a build-time
circular-dependency error) and by `MultiChannelAgent.ArchitectureTests`.

## Prerequisites

- .NET 10 SDK
- Node.js 22+ and npm (for the web client)
- Docker (only required for `MultiChannelAgent.IntegrationTests`; see below if Docker is unavailable)

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
# Pure domain/application unit tests, architecture checks, and (if Docker is available) the
# SQL-backed application-boundary scenario, in one pass:
dotnet test

# Just the web client:
cd src/web && npm run lint && npm run build
```

`MultiChannelAgent.IntegrationTests` boots an ephemeral SQL Server container via Testcontainers,
applies the production EF Core migrations, and submits a synthetic Turn through the real HTTP
endpoints, driving the hosted-worker duties deterministically (via their internal one-shot
operations, not sleeps) instead of waiting on the periodic background loop. If Docker is not
available, these tests report **Skipped** (not failed) with a clear reason; the same tests run for
real in CI, where GitHub-hosted runners provide Docker.

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

Then:

- `GET /health/live` — liveness (no dependencies).
- `GET /health/ready` — readiness (checks SQL Server connectivity).
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
docker run -p 8080:8080 -e ConnectionStrings__MultiChannelAgent="<connection string>" multi-channel-agent
```

The multi-stage `Dockerfile` builds the web client (Node), publishes the backend (.NET SDK), and
combines both into a single ASP.NET Core runtime image that serves the API, health endpoints, and the
built SPA from one origin.

## Continuous integration

`.github/workflows/ci.yml` runs on every push/PR:

- **backend**: restore, build (`TreatWarningsAsErrors`), EF Core migration script validation
  (`dotnet ef migrations script --idempotent`), and `dotnet test` — which covers the domain/application
  unit tests, the architecture tests, and the SQL-backed application-boundary scenario (Docker is
  preinstalled on GitHub-hosted runners).
- **frontend**: `npm ci`, lint, and build for the web client.
