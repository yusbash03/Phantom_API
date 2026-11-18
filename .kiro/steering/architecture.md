---
inclusion: always
---

# PHANTOM — Architecture Steering

Full design: `.kiro/specs/phantom-platform/design.md`. This file is the short form that every
change must respect.

## Repositories (two independent Git repos, one Kiro multi-root workspace)

| Repo | Local path | Owns |
|---|---|---|
| `Phantom_API` | `/Users/yusufbashir/Documents/repos/backend/Phantom_API` | .NET solution, migrations, backend tests, **canonical Kiro specs & steering**, OpenAPI contract (`contracts/openapi.json`) |
| `Phantom_UI` | `/Users/yusufbashir/Documents/repos/frontend/Phantom_UI` | React app, frontend tests, Playwright e2e, frontend steering pointer |

Never nest one repo in the other, never create a third app, never move either repo without approval.

## Backend: modular monolith, Clean Architecture

```
src/PHANTOM.API            → controllers, hubs, middleware, DI composition, OpenAPI
src/PHANTOM.Application    → use cases, DTOs, validators, ports (interfaces), authorization policies
src/PHANTOM.Domain         → entities, value objects, enums, state machines, domain rules (no dependencies)
src/PHANTOM.Infrastructure → EF Core/Npgsql, migrations, Gemini, Mapbox, Open-Meteo, storage, JWT, background workers
tests/PHANTOM.UnitTests
tests/PHANTOM.IntegrationTests
```

Dependency direction: `API → Application → Domain`; `Infrastructure → Application, Domain`.
`API` references `Infrastructure` only in the composition root (`Program.cs` / DI extension).
The Domain project must have **zero** package references to EF Core, ASP.NET, or HTTP clients.

Modules are folders inside each layer (e.g. `Application/Incidents`, `Application/Correlation`),
not separate projects. Keep a module's code together.

## Rules

- Controllers are thin: bind → call one application service/handler → map result to HTTP. No business logic.
- Entities are never returned from the API. Every endpoint has request/response DTOs.
- State transitions (incident status, assignment status) live in the Domain and are the only
  way to change status. No `incident.Status = ...` outside the domain.
- Every external integration sits behind an Application interface:
  `IIncidentIntelligenceService`, `IGeocodingService`, `IWeatherService`, `IObjectStorage`,
  `ISpeechToTextService`, `IRealtimeNotifier`, `IClock`.
- Development-only fakes must be named `Simulated*`, registered only when
  `ASPNETCORE_ENVIRONMENT=Development` **and** the real provider is unconfigured, and must mark
  their output as simulated. They are never registered in Production.
- Asynchronous work (AI analysis, geocoding, weather) runs in `BackgroundService` workers that
  read a DB-backed work state (e.g. `IncidentAnalysis.Status = Pending`), so work survives restarts.
- No Redis, Kafka, Kubernetes or microservices.
- All timestamps UTC (`timestamptz`). IDs are UUIDs (UUIDv7 via `Guid.CreateVersion7()`).
- Audit entries are written in the **same transaction** as the change they describe.

## Local communication

- API: `http://localhost:5080` (`/api/v1/*`, SignalR hub `/hubs/operations`, OpenAPI `/openapi/v1.json`, Swagger UI `/swagger`).
- UI: Vite dev server `http://localhost:5173`, which **proxies** `/api` and `/hubs` (with `ws: true`)
  to the API. The browser sees one origin, so the refresh-token cookie is first-party.
- PostgreSQL: Neon (connection string in `dotnet user-secrets`, key `ConnectionStrings:Phantom`). `docker compose up -d` provides an optional offline PostgreSQL 17.

## API contract

The backend publishes OpenAPI at build time to `contracts/openapi.json` (committed). The frontend
generates TypeScript types from it (`npm run api:generate`) into `src/api/generated/` and never
hand-writes DTO shapes that exist in the contract. A contract change is a change in both repos.
