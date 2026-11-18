# Implementation Plan — PHANTOM Platform

Rules: a task is ticked only when its acceptance criteria are verified (passing test or recorded
manual check). `[API]` = Phantom_API repo, `[UI]` = Phantom_UI repo. P0 work precedes P1.
Do not start a Phase 3 feature while the previous one has unresolved critical failures.

## Phase 1 — Discovery and specification

- [x] 0.1 Inspect workspace, both repositories, toolchain (.NET 10.0.400, Node 26.7.0, Docker 29.7.2, Kiro 1.4.215)
- [x] 0.2 Draft requirements.md, design.md, tasks.md and steering documents
- [x] 0.3 Approval of design decisions D1–D6 (design.md §16) — approved 2026-11-18; D3 = Render
- [ ] 0.4 Prerequisites: ~~start Docker Desktop~~ (done); ~~database~~ (Neon PostgreSQL provided); **obtain Gemini API key; obtain Mapbox account (secret + URL-restricted public token)** — pending, needed before tasks 17 and 21

## Phase 2 — Foundation

- [x] 1. Backend solution skeleton [API]
  - Create `PHANTOM.sln`, the six projects, `Directory.Build.props` (net10.0, nullable, warnings-as-errors, analyzers), `.gitignore`, `.editorconfig`
  - Enforce dependency direction with an architecture test (reflection over assembly references)
  - _Requirements: 19.3; architecture steering_
- [x] 2. Local infrastructure [API]
  - Development database is Neon (connection in `dotnet user-secrets`); `docker-compose.yml` is an optional offline alternative
  - `docker-compose.yml` (PostgreSQL 17, healthcheck, named volume), `.env.example` with placeholders
  - `appsettings.json` + options classes with `ValidateOnStart`
  - _Requirements: 19.3_
- [x] 3. Domain core [API]
  - [x] 3.1 Enums, `Location` value object with verification invariant; unit tests
    - _Requirements: 5.1, 5.2, 8.1, 8.6_
  - [x] 3.2 `Incident` aggregate and `IncidentStatusPolicy` transition table; FsCheck property test over all status pairs
    - _Requirements: 5.3, 5.4, 5.5, 5.6, 5.7_
  - [x] 3.3 `ResponderAssignment` and `AssignmentStatusPolicy`; property tests
    - _Requirements: 11.3_
- [x] 4. Persistence [API]
  - Initial migration covers identity, refresh tokens and audit; incident/responder tables are added by their Phase 3 tasks
  - DbContext, entity configurations (snake_case, xmin concurrency, indexes, partial unique indexes), incident-number sequence
  - Initial migration incl. audit-log UPDATE/DELETE trigger and role seed
  - Integration test fixture: Testcontainers PostgreSQL + `WebApplicationFactory`
  - Test: migrations apply to an empty database; audit UPDATE/DELETE rejected
  - _Requirements: 15.3, 5.10_
- [x] 5. Cross-cutting API [API]
  - ProblemDetails/`IExceptionHandler`, correlation ID middleware, Serilog, security headers, CORS from config, health checks, built-in OpenAPI + Swagger UI, build-time export to `contracts/openapi.json`
  - _Requirements: 19.1, 19.2, 19.4, 19.5, 19.6_
- [x] 6. Audit writer [API]
  - `IAuditWriter` adding entries to the current unit of work; test that a rolled-back transaction leaves no audit row and a committed one does
  - _Requirements: 15.1, 15.2_
- [x] 7. Authentication [API]
  - Login, refresh (rotation + family reuse detection), logout, `/auth/me`; JWT options; login rate limiting; login audit
  - Tests: valid/invalid login, uniform error, rotation, reuse revokes family, deactivated user rejected
  - _Requirements: 1.1–1.8_
- [x] 8. Authorisation [API]
  - Matrix currently covers auth and user endpoints; every new endpoint is picked up automatically by the anonymous-caller test
  - Fallback policy (authenticated), named role policies, `ICurrentUser`
  - Authorisation matrix integration test: every endpoint × {anonymous, Responder, Coordinator, Administrator}
  - _Requirements: 2.1–2.6_
- [x] 9. User management [API]
  - Admin CRUD for users/roles, last-admin protection, audit
  - _Requirements: 3.1–3.4, 2.5, 2.6_
- [ ] 10. Development seeding [API]
  - Demo users per role and the environment guard are done and tested; **demo responders arrive with task 23**
  - Demo users per role, responders; `is_demo_data = true`; environment guard
  - Test: seeding is a no-op in Production without the explicit flag
  - _Requirements: 20.1, 20.2_
- [x] 11. Frontend skeleton [UI]
  - Linter is oxlint (current Vite template default) instead of ESLint; TypeScript pinned to 5.9 because openapi-typescript requires the TS 5 compiler API
  - Vite + React + TS (strict), Tailwind, shadcn/ui, Router, TanStack Query, RHF + Zod, Vitest/RTL, Playwright, ESLint; `npm run typecheck`, `npm run api:generate` (openapi-typescript from `../../backend/Phantom_API/contracts/openapi.json`, overridable by `PHANTOM_API_CONTRACT`)
  - Vite proxy for `/api` and `/hubs` (ws)
  - `.kiro/steering/frontend.md` already present
  - _Requirements: 18.1; architecture steering_
- [ ] 12. Design system and app shell [UI]
  - Done: theme tokens, Inter, Sidebar, TopNav, SeverityBadge (+tests), Loading/Error/Empty states, ConfirmDialog (+tests), PageHeader. **Remaining:** MetricCard (task 27), StatusBadge/DataTable/Filters (task 16), Timeline (task 26) — built with their first real consumer
  - Theme tokens (navy/charcoal, orange accent, severity palette), Inter font, Sidebar, TopNav, MetricCard, SeverityBadge (icon + text), StatusBadge, DataTable, Filters, ConfirmDialog, Loading/Error/Empty states, Timeline
  - Component tests for SeverityBadge accessibility and ConfirmDialog
  - _Requirements: 18.3, 18.5, 18.6, 18.7_
- [x] 13. Auth in the SPA [UI]
  - Login page, in-memory token, single-flight refresh on 401, session restore on load, role-aware navigation, protected routes
  - _Requirements: 1.1, 1.3, 18.2_
- [x] 14. Phase 2 verification checkpoint (2026-11-18: 29 unit, 29 integration, 12 Vitest, 8 Playwright tests passing)
  - Backend builds with warnings-as-errors; unit + integration tests pass; migrations apply; frontend lint/typecheck/test/build pass; login works end-to-end through the Vite proxy for all three roles; role restriction verified via API

## Phase 3 — Core functionality (P0 first, in order)

- [ ] 15. Incident intake [API + UI]
  - Multipart endpoint, validation, idempotency, rate limit, magic-byte sniffing, SkiaSharp re-encode, `IObjectStorage` (local), reference generation, public status endpoint; public report form (mobile-first) with confirmation screen
  - Tests: invalid input persists nothing; idempotent replay; disallowed file rejected; anonymous cannot reach staff endpoints
  - _Requirements: 4.1–4.10_
- [ ] 16. Incident list and detail [API + UI]
  - Query endpoints with filters/pagination, edit, transition endpoints, severity, concurrency conflict handling; list and detail screens
  - _Requirements: 5.1–5.11, 18.1_
- [ ] 17. AI intelligence service [API]
  - `IIncidentIntelligenceService`, `GeminiIntelligenceService` (REST, structured output), `AnalysisWorker` with SKIP LOCKED claim and stuck-job recovery, retry endpoint
  - **Verify against the live Gemini API** and record the result; until then report as pending
  - _Requirements: 6.1–6.9_
- [ ] 18. AI output validation [API]
  - `IntelligenceResultValidator`; tests for malformed JSON, invalid enums, oversize strings, provider timeout → Failed with report intact; property test: analysis never changes approved severity
  - _Requirements: 6.3, 6.4, 6.7_
- [ ] 19. Intelligence panel [UI]
  - AI recommendation panel (clearly labelled), failure state with retry, side-by-side AI vs approved values
  - _Requirements: 6.12, 15.4_
- [ ] 20. Incident correlation [API + UI]
  - Pure `CorrelationScorer` with FsCheck properties (bounds, symmetry, monotonicity) and edge-case tests (missing coords, identical text, opposite categories, window boundaries); `CorrelationService`; confirm/unrelated/related decisions; possible-duplicates UI
  - _Requirements: 7.1–7.8_
- [ ] 21. Geocoding [API]
  - `MapboxGeocodingService`; worker step; failure → needs-verification flag; location verification endpoint; test that only manual verification sets `isVerified`
  - **Verify against live Mapbox** and record the result
  - _Requirements: 8.1–8.6_
- [ ] 22. Operations map [UI]
  - Mapbox GL map, category/severity/status filters, verified vs unverified styling, provenance popup, select → detail
  - _Requirements: 8.7, 8.8, 12.3_
- [ ] 23. Responder management [API + UI]
  - _Requirements: 10.1–10.4_
- [ ] 24. Assignment workflow [API + UI]
  - Assign/cancel (coordinator), acknowledge/decline/status/updates/complete (assignee only), system-driven incident transitions, "My assignments" screen
  - Tests: non-assignee gets 404; AI never creates assignments; transition properties
  - _Requirements: 11.1–11.8_
- [ ] 25. Notifications (in-app) [API + UI]
  - _Requirements: 14.1–14.3_
- [ ] 26. Incident history timeline and audit search [API + UI]
  - _Requirements: 15.1–15.6_
- [ ] 27. Dashboard metrics [API + UI]
  - Metrics from DB; recent activity; mini map; test metrics against seeded fixtures
  - _Requirements: 12.1–12.3_
- [ ] 28. P0 checkpoint: Playwright e2e of the demonstration scenario (design.md §12) passes against real API + PostgreSQL

### P1 (only after task 28 passes)

- [ ] 29. SignalR real-time with reconnect + polling fallback — _Requirements: 13.1–13.4, 2.7_
- [ ] 30. Weather enrichment (Open-Meteo) with snapshots and cache — _Requirements: 9.1–9.6_
- [ ] 31. Image analysis via Gemini multimodal — _Requirements: 6.10_
- [ ] 32. Voice recording + transcription — _Requirements: 4.1, 6.11_
- [ ] 33. Reports and CSV export — _Requirements: 16.1, 16.2_
- [ ] 34. Application settings — _Requirements: 17.1–17.3_

## Phase 4 — Quality and delivery

- [ ] 35. Security review (phantom-security-reviewer agent) of auth, uploads, IDOR, CORS, headers, secrets; fix findings
- [ ] 36. Responsive and accessibility pass; loading/empty/error coverage audit
- [ ] 37. Deployment: Dockerfile, Render (or Azure) service, managed PostgreSQL, migration bundle release step, R2 storage, Vercel with rewrites, environment variables, CORS; verify `/health/ready` publicly
- [ ] 38. Demo data for judging deployment (labelled) and recorded weather/AI fixtures (labelled)
- [ ] 39. README (both repos), API docs, environment docs, LICENSE (MIT), demo script, `docs/kiro-usage.md` finalised from the actual log
- [ ] 40. Final verification of the public deployment against the demonstration scenario
