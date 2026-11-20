# Implementation Plan — PHANTOM Platform

Rules: a task is ticked only when its acceptance criteria are verified (passing test or recorded
manual check). `[API]` = Phantom_API repo, `[UI]` = Phantom_UI repo. P0 work precedes P1.
Do not start a Phase 3 feature while the previous one has unresolved critical failures.

## Phase 1 — Discovery and specification

- [x] 0.1 Inspect workspace, both repositories, toolchain (.NET 10.0.400, Node 26.7.0, Docker 29.7.2, Kiro 1.4.215)
- [x] 0.2 Draft requirements.md, design.md, tasks.md and steering documents
- [x] 0.3 Approval of design decisions D1–D6 (design.md §16) — approved 2026-11-18; D3 = Render
- [ ] 0.4 Prerequisites: ~~start Docker Desktop~~ (done); ~~database~~ (Neon PostgreSQL provided); ~~obtain Gemini API key~~ (configured in user-secrets 2026-11-19); **Mapbox account (secret + URL-restricted public token)** — `Mapbox:AccessToken` (public `pk.` token) works for geocoding; see task 21 note

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
- [x] 10. Development seeding [API]
  - Demo users per role and the environment guard were done and tested earlier. Demo responders (5, labelled "(demo)", `IsDemoData`, MED-01 linked to the demo Responder account) were added with task 23 and verified 2026-11-20 by an integration test, including idempotency.
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
  - Done: theme tokens, Inter, Sidebar, TopNav, SeverityBadge (+tests), Loading/Error/Empty states, ConfirmDialog (+tests), PageHeader. **Remaining:** MetricCard (task 27), Timeline (task 26). StatusBadge, LocationBadge, Pagination and the list filters shipped with task 16; plain shadcn `Table` is used instead of a generic DataTable — built with their first real consumer
  - Theme tokens (navy/charcoal, orange accent, severity palette), Inter font, Sidebar, TopNav, MetricCard, SeverityBadge (icon + text), StatusBadge, DataTable, Filters, ConfirmDialog, Loading/Error/Empty states, Timeline
  - Component tests for SeverityBadge accessibility and ConfirmDialog
  - _Requirements: 18.3, 18.5, 18.6, 18.7_
- [x] 13. Auth in the SPA [UI]
  - Login page, in-memory token, single-flight refresh on 401, session restore on load, role-aware navigation, protected routes
  - _Requirements: 1.1, 1.3, 18.2_
- [x] 14. Phase 2 verification checkpoint (2026-11-18: 29 unit, 29 integration, 12 Vitest, 8 Playwright tests passing)
  - Backend builds with warnings-as-errors; unit + integration tests pass; migrations apply; frontend lint/typecheck/test/build pass; login works end-to-end through the Vite proxy for all three roles; role restriction verified via API

## Phase 3 — Core functionality (P0 first, in order)

- [x] 15. Incident intake [API + UI]
  - Verified 2026-11-19: 47 unit + 51 integration (22 intake) + 21 Vitest tests passing; manual check through the Vite proxy against Neon — submit (201, `PH-2026-000001`), idempotent replay (same reference/timestamp), coarse status, disguised file rejected (400, `errors.files`), stored JPEG re-encoded with no EXIF
  - AI analysis rows are created `Pending` and stay queued until the worker lands in task 17. The SkiaSharp Linux native assets are referenced for the Render image. The client-IP hash salt is a code constant, to be revisited in task 35.
  - Multipart endpoint, validation, idempotency, rate limit, magic-byte sniffing, SkiaSharp re-encode, `IObjectStorage` (local), reference generation, public status endpoint; public report form (mobile-first) with confirmation screen
  - Tests: invalid input persists nothing; idempotent replay; disallowed file rejected; anonymous cannot reach staff endpoints
  - _Requirements: 4.1–4.10_
- [x] 16. Incident list and detail [API + UI]
  - Verified 2026-11-19: 47 unit + 67 integration (16 new) + 30 Vitest (9 new) + 12 Playwright (desktop + mobile, real API + Neon) tests passing
  - The `assigned/unassigned` list filter (5.11) was delivered with task 24. Close-as-duplicate was delivered with task 20.
  - The attachment download endpoint (`GET /attachments/{id}/content`, staff, `Content-Disposition: attachment`, `nosniff`) is delivered here
  - Query endpoints with filters/pagination, edit, transition endpoints, severity, concurrency conflict handling; list and detail screens
  - _Requirements: 5.1–5.11, 18.1_
- [x] 17. AI intelligence service [API]
  - Implemented and tested 2026-11-19: 83 unit (incl. Gemini adapter contract tests against the recorded response shape) + 76 integration tests passing, stable over 3 runs. The worker, retry endpoint, `Simulated*` dev provider and the human-only incident `version` (AI writes no longer cause false 409s) are all in place.
  - **Live verification PASSED 2026-11-20:** the opt-in live test returned a valid `phantom.intel.v1` result from `gemini-3.8-flash` (fire with people trapped → Critical, trapped = Reported, accurate summary). The running app also completed real analyses. On 2026-11-19 the provider had returned transient 404/503 responses. Real latency was 13-30+ s, so the per-attempt timeout default was raised from the design's 30 s to 60 s (`Ai:TimeoutSeconds`).
  - `IIncidentIntelligenceService`, `GeminiIntelligenceService` (REST, structured output), `AnalysisWorker` with SKIP LOCKED claim and stuck-job recovery, retry endpoint
  - **Verify against the live Gemini API** and record the result; until then report as pending
  - _Requirements: 6.1–6.9_
- [x] 18. AI output validation [API]
  - Verified 2026-11-19: `IntelligenceResultParser`/`IntelligenceResultValidator` with a strict enum converter (rejects numbers and flag combinations such as "High, Low"). Unit tests cover malformed JSON, invalid enums, missing/unknown fields and oversize strings/lists. Integration tests cover timeout, invalid output and adapter exceptions (each leaves the analysis Failed with the report intact) and AI results arriving after verification. The approved-severity property test is `AiRecommendationIsolationTests`.
  - `IntelligenceResultValidator`; tests for malformed JSON, invalid enums, oversize strings, provider timeout → Failed with report intact; property test: analysis never changes approved severity
  - _Requirements: 6.3, 6.4, 6.7_
- [x] 19. Intelligence panel [UI]
  - Verified 2026-11-20: 37 Vitest tests (7 new) cover advisory labelling, the AI vs approved comparison table, plain-text rendering of hostile model output, the Simulated badge, failure with retry, keeping the last success visible, and polling until a result arrives (which then refreshes the incident). 14 Playwright tests pass (desktop + mobile, real API + Neon), including one asserting an honest panel state for a new report.
  - Playwright now runs with one worker: parallel browsers starved the local machine of CPU (password hashing went from ~1 s to 10+ s). The suite also has a global warm-up step.
  - About 100 analyses from earlier e2e/manual reports are still Pending in the Neon dev database. They will be processed through Gemini the next time the API runs with the worker enabled.
  - AI recommendation panel (clearly labelled), failure state with retry, side-by-side AI vs approved values
  - _Requirements: 6.12, 15.4_
- [x] 20. Incident correlation [API + UI]
  - Verified 2026-11-20: 105 unit tests. FsCheck properties cover bounded, symmetric, monotone and identical-pair-meets-threshold. Edge cases cover missing coordinates, identical text, opposite and "Other" categories, window and radius boundaries, sparse evidence, and street words not counting as shared places. 83 integration tests (7 new, stable over 3 runs), 44 Vitest (7 new) and 16 Playwright tests (desktop + mobile, incl. report → suggestion → confirm duplicate) all pass.
  - Correlation runs best-effort on intake and after AI analysis. It never changes, merges or hides anything; only coordinator decisions do. A confirmed duplicate keeps every report: reports move to the primary with `OriginalIncidentId` preserved, each move is audited, and the closed incident stays readable. Unrelated pairs are never re-suggested.
  - Weights and thresholds come from configuration (`Correlation:*`, validated on start); the admin screen is task 34. Added `PrimaryIncidentId` on relations (not in the design) to record which incident stays open.
  - Pure `CorrelationScorer` with FsCheck properties (bounds, symmetry, monotonicity) and edge-case tests (missing coords, identical text, opposite categories, window boundaries); `CorrelationService`; confirm/unrelated/related decisions; possible-duplicates UI
  - _Requirements: 7.1–7.8_
- [x] 21. Geocoding [API]
  - Verified 2026-11-20: 120 unit tests, including the critical property 4 FsCheck property (no sequence of system operations yields a verified location) and Mapbox adapter tests against the recorded v6 shape. 91 integration tests (8 new) pass, stable over 3 runs. 45 Vitest and 16 Playwright tests pass.
  - **Live Mapbox PASSED:** the opt-in test placed "Allen Avenue, Ikeja, Lagos" at Street precision, and a nonsense query gave no result. In the running app a report with no coordinates was geocoded to 6.603149, 3.351186 (Geocoded, Street, unverified, match shown), and the token appeared in no log line.
  - Precision is conservative: geocoded points are never "Exact", and region/country matches count as no result. There is no simulated geocoder (8.4). AI-extracted place names are geocoded as `AiInferred` when the text fails. A race where an AI result arriving mid-geocode lost its place names was found and fixed (geocoding status is a concurrency token; re-queue supersedes in-flight attempts).
  - Added `PUT /incidents/{id}/location` plus a small "Verify location" dialog, since the new action needed UI.
  - `Mapbox:AccessToken` is a **public (`pk.`) token**; Mapbox accepts it for server-side geocoding. Decide in task 35 whether production uses a secret server token plus a separate URL-restricted public token for the map (task 22).
  - `MapboxGeocodingService`; worker step; failure → needs-verification flag; location verification endpoint; test that only manual verification sets `isVerified`
  - **Verify against live Mapbox** and record the result
  - _Requirements: 8.1–8.6_
- [x] 22. Operations map [UI]
  - Verified 2026-11-20: 94 integration tests (3 new, for `GET /incidents/map`), 52 Vitest (7 new) and 20 Playwright tests (desktop + mobile) pass. Playwright exercises the real Mapbox map in Chromium (canvas rendered, a new report's marker found by its spoken label, details card with provenance, navigation to the incident).
  - Added the backend `GET /incidents/map` (staff), which returns a typed list rather than the design's GeoJSON. It also returns a count of matching incidents without coordinates, so the map never implies it shows everything.
  - Markers are accessible HTML buttons with full spoken labels. Unverified locations use a dashed ring and an "Unverified" tag; colour comes from approved severity only. There is a side list as a keyboard alternative. Mapbox GL (`mapbox-gl` pinned 3.34.2) is lazy-loaded. A bug where Mapbox set `role="img"` on the button markers was found and fixed.
  - Known limitation: markers at (near-)identical coordinates overlap. The side list handles selection; marker clustering is a possible later improvement. Restrict `VITE_MAPBOX_PUBLIC_TOKEN` to the app's URLs in the Mapbox dashboard before deployment (task 37).
  - Mapbox GL map, category/severity/status filters, verified vs unverified styling, provenance popup, select → detail
  - _Requirements: 8.7, 8.8, 12.3_
- [x] 23. Responder management [API + UI]
  - Verified 2026-11-20: 134 unit tests (14 new) and 102 integration tests (8 new: role matrix, validation, case-insensitive unique call signs, account-linking rules, stale versions, audit before/after, all list filters plus facets, deactivate/reactivate, self-service availability, demo seeding). 59 Vitest (7 new) and 26 Playwright tests (6 new) pass.
  - Admins create, edit, deactivate and reactivate responders and set their availability. Coordinators read only. A responder sets their own Available/Unavailable on the Account page. "Assigned" can never be set by hand (10.4).
  - **Hand-off to task 24:** `ResponderService.HasActiveAssignmentAsync` returns false until assignments exist. Task 24 must replace it with a real query and set Assigned or release availability from assignment events.
  - _Requirements: 10.1–10.4_
- [x] 24. Assignment workflow [API + UI]
  - Verified 2026-11-20: 141 unit tests pass (7 new), including a bytecode scan proving `AssignmentService` is the only caller of `ResponderAssignment.Create` (11.7), constructor checks showing AI, correlation and geocoding cannot reach assignments, and the domain refusing an unidentified creator. 109 integration tests pass (7 new), stable over 3 runs: lifecycle with System-audited incident transitions, 404 for non-assignees (11.5, critical property 5), override rules (11.2), decline/cancel reverting the incident, ordered steps, the assigned/unassigned list filter, and no assignment from a Critical AI result. 66 Vitest (7 new) and 28 Playwright tests (2 new: full coordinator → responder → completion flow on desktop and mobile) pass.
  - Completed the task 16 hand-off (5.11 assigned/unassigned filter) and the task 23 hand-off (`HasActiveAssignmentAsync` is now a real query; responders move to and from Assigned automatically).
  - **Regression from task 21 found and fixed:** using `geocoding_status` as a concurrency token gave coordinators false 409s on fresh incidents. It was replaced by a `geocoding_generation` counter bumped only on re-queue. Two further geocoding bugs found and fixed: the AI-side re-queue acted on a stale tracked entity (now one conditional UPDATE), and a superseded attempt could leave a claim stuck in Processing (now released). Vitest `testTimeout` was raised to 15 s because UI tests timed out under background load.
  - Responders now land on My assignments after sign-in. "Notify the responder" (11.1) means the assignment appears on My assignments (polled every 30 s); push/in-app notifications are task 25.
  - Assign/cancel (coordinator), acknowledge/decline/status/updates/complete (assignee only), system-driven incident transitions, "My assignments" screen
  - Tests: non-assignee gets 404; AI never creates assignments; transition properties
  - _Requirements: 11.1–11.8_
- [x] 25. Notifications (in-app) [API + UI]
  - Verified 2026-11-20: 144 unit tests (3 new) and 114 integration tests (5 new, stable over 3 runs). The integration tests cover all six triggers reaching exactly the right people, no notification for unlinked responders, Critical alerts sent once and never for High, and own-notifications-only access with 404 for others. 72 Vitest (6 new) and 30 Playwright tests (2 new: assignment notice to the responder → My assignments; decline notice to the coordinator → incident) pass.
  - Notifications are written in the same transaction as the event that causes them: assigned/cancelled to the responder's linked account; Critical AI recommendation, possible duplicate, decline and completion to every active coordinator. They contain incident numbers and titles only, never reporter contact details.
  - The UI has a top-bar bell with the unread count in its accessible name, a recent list, mark read / mark all read, and a full Notifications page. It polls every 30 s; SignalR push is task 29. A double full stop in decline/completion messages was found by e2e and fixed.
  - _Requirements: 14.1–14.3_
- [x] 26. Incident history timeline and audit search [API + UI]
  - Verified 2026-11-20: 144 unit tests and 119 integration tests (5 new, stable over 3 runs). The new tests cover full-lifecycle timeline order, duplicate timelines (reports kept), staff-only timeline access, audit search filters, and audit routes that are GET-only. 77 Vitest (5 new) and 32 Playwright tests (2 new: the History lists report, AI-labelled analysis and verify decision; an admin finds the decision in the audit log by entity and action) pass.
  - `GET /incidents/{id}/timeline` (staff) merges audit entries for the incident, its reports, analyses, assignments and relations with responder updates. Each entry gets a plain-language summary and detail; AI entries are flagged and drawn dashed with an "AI" badge, and automatic status changes say why. Ordering is by time, then by audit id. To make the order read cause → effect, assignment transitions are now audited before their consequences.
  - `GET /audit` and `/audit/facets` (Administrator) filter by actor, entity type/id, action and date range, with before/after JSON shown on expand. There are no write endpoints; the append-only enforcement from task 3 is unchanged.
  - _Requirements: 15.1–15.6_
- [x] 27. Dashboard metrics [API + UI]
  - Metrics from DB; recent activity; mini map; test metrics against seeded fixtures
  - Verified 2026-11-20: 144 unit tests and 122 integration tests (3 new, stable over 3 runs). The integration tests check every metric against a direct database count, before and after a known set of fixtures (reported, verified Critical, rejected, then assigned), with exact deltas. They also check that activity is newest first, names its incident, excludes user administration and is capped at 50, and that only staff can access the dashboard. 81 Vitest (4 new) and 34 Playwright tests (2 new: counts rise after a public report, which then appears in recent activity and links to the incident; the map renders) pass.
  - `GET /dashboard/summary` returns the seven counts from Requirement 12.1 in three queries at request time, with `generatedAt`. "Verified, unassigned" means status Verified, because the first assignment moves an incident to Assigned and ending every assignment moves it back. `GET /dashboard/activity` reuses the timeline wording, so AI entries stay flagged.
  - UI: staff now land on `/dashboard`. Tiles link to matching lists where an exact filter exists and are highlighted when they need attention. Recent activity and the lazy-loaded Mapbox mini map of open incidents (with a "without coordinates" note) are shown. The page refreshes every 30 s until SignalR (task 29). An error state is shown instead of placeholder numbers.
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
