# PHANTOM API

PHANTOM is an AI-assisted incident intelligence and response coordination platform. Members of the public report
incidents. AI structures and assesses each report as a **recommendation**, and trained coordinators verify,
prioritise, link duplicates, assign responders and resolve. Every significant action is audited.

This repository is the .NET backend. The web app is in the companion repository **Phantom_UI**.

> PHANTOM is a decision-support tool. It does not dispatch services, diagnose anyone, or decide anything on its own.
> It is not certified, compliant or approved for production emergency use.

## Principles

1. **Human authority.** AI never changes approved severity, status, assignments or resolution. Those are coordinator decisions, attributed in the audit log.
2. **AI output is advisory.** It is stored apart from approved values and labelled "AI recommendation" everywhere.
3. **Nothing is silently lost.** Original reports are immutable. Duplicates are suggested, never auto-merged. A failed analysis leaves the report intact and retryable.
4. **Honest data.** Locations show how they were obtained (reported, geocoded, AI-inferred, verified). Simulated, recorded-fixture and demo data are labelled as such.
5. **Everything significant is audited.** The audit log is append-only, enforced in the database.

## Features

| Area | What it does |
|---|---|
| Public reporting | Anonymous reports with optional coordinates, photos and a voice note. Idempotent submission, rate limits, content-sniffed uploads, and photos re-encoded to strip metadata. |
| AI intelligence | Gemini structures each report: category, severity, hazards, missing information, next actions. Strict schema validation (`phantom.intel.v1`), photos and the voice transcript included, retries and honest failures. |
| Incidents | Coordinator workflow (review, verify with an approved severity, reject, resolve, reopen, close), optimistic concurrency, and location verification. |
| Duplicates | Deterministic correlation (distance, time, category, text, place names) suggests possible duplicates. Coordinators confirm, relate or dismiss. |
| Map and geocoding | Mapbox forward geocoding with labelled precision. The map endpoint states how many incidents cannot be plotted. |
| Responders and assignments | Profiles, availability, assignment with override reasons, and responder acknowledgement and progress. |
| Notifications and real time | In-app notifications, plus SignalR change events published after commit to authorised groups only. |
| Weather | Open-Meteo context for incidents with coordinates: cached, time-limited, never presented as evidence. |
| History and audit | Plain-language incident timeline and administrator audit search. |
| Dashboard and reports | Live counts, recent activity, and period reports (medians, AI agreement) with CSV export. |
| Settings | Administrator-adjustable correlation and weather values, validated and audited. |

## Architecture

Clean Architecture on .NET 10:

```
src/
  PHANTOM.Domain          entities, state machines, rules (no dependencies)
  PHANTOM.Application     use cases, validation, Result<T>/Error, interfaces for providers
  PHANTOM.Infrastructure  EF Core + PostgreSQL, Gemini, Mapbox, Open-Meteo, storage (local / S3), workers, SignalR change capture
  PHANTOM.API             controllers, auth (JWT + rotating refresh cookie), ProblemDetails, rate limits, SignalR hub
tests/
  PHANTOM.UnitTests        xUnit v3 + FsCheck
  PHANTOM.IntegrationTests the real API against PostgreSQL 17 in Testcontainers
contracts/openapi.json    generated at build; the web app generates its TypeScript types from it
```

The full design is in `.kiro/specs/phantom-platform/design.md`, the requirements in `requirements.md`, and the work log in `tasks.md`.

## Getting started

Prerequisites:
- .NET SDK **10.0.400** (see `global.json`);
- Docker, for the integration tests;
- a PostgreSQL database: Neon (the default for development) or `docker compose up -d` for a local one.

```bash
cd src/PHANTOM.API
dotnet user-secrets set "ConnectionStrings:Phantom" "Host=...;Database=...;Username=...;Password=...;SSL Mode=Require"
dotnet user-secrets set "Jwt:SigningKey" "$(openssl rand -base64 48)"
dotnet user-secrets set "Seed:DemoUserPassword" "<at least 12 characters>"
# Optional integrations (without them the app degrades honestly; see docs/configuration.md):
dotnet user-secrets set "Ai:ApiKey" "<gemini-api-key>"
dotnet user-secrets set "Mapbox:AccessToken" "<mapbox-server-token>"
cd ../..
dotnet run --project src/PHANTOM.API --launch-profile http   # http://localhost:5080
```

In Development the API:
- applies migrations on startup;
- seeds the labelled demo accounts (`admin@`, `coordinator@` and `responder@phantom.demo`, using your demo password), demo responders and demo incidents;
- serves Swagger UI at `/swagger`.

Without a Gemini key, Development uses a clearly labelled **simulated** AI.

## Tests

```bash
dotnet test tests/PHANTOM.UnitTests          # fast, no external services
dotnet test tests/PHANTOM.IntegrationTests   # needs Docker (PostgreSQL 17 and S3Mock containers)
PHANTOM_LIVE_AI_KEY=... dotnet test --project tests/PHANTOM.UnitTests --filter-trait "Category=Live"   # optional live Gemini check
```

Integration tests use scripted AI, geocoding, weather and transcription providers, so they never call external
services. Browser end-to-end tests live in Phantom_UI (`npm run test:e2e`).

## Documentation

- [API reference](docs/api.md): endpoints, access rules, errors, real-time events. Interactive: `/swagger` in Development.
- [Configuration](docs/configuration.md): every setting, its default, and which are secrets.
- [Deployment](docs/deployment.md): Neon, Cloudflare R2, Render and Vercel, plus the judging deployment.
- [Demo script](docs/demo-script.md): a walkthrough of the demonstration scenario.
- [Security review](docs/security-review.md): what was checked, fixed and accepted.
- [Tool usage log](docs/kiro-usage.md): how Kiro and Claude Code were used.

## Licence

[MIT](LICENSE)
