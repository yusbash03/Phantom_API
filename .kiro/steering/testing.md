---
inclusion: fileMatch
fileMatchPattern: "**/*{Tests,tests,.test,.spec,e2e}*/**"
---

# PHANTOM — Testing Steering

Never disable, skip, or weaken a failing test to make a build green. Fix the code or report the failure.

## Backend (xUnit)

| Project | Covers | Infrastructure |
|---|---|---|
| `PHANTOM.UnitTests` | Domain state machines, value objects, correlation scoring, AI output validation, application services with fakes | none |
| `PHANTOM.IntegrationTests` | API endpoints, authorization matrix, EF persistence, migrations, audit writes | `WebApplicationFactory` + Testcontainers PostgreSQL (requires Docker) |

- Assertions: xUnit `Assert` (no paid assertion libraries).
- Property-based tests with **FsCheck** for: incident status transitions, assignment transitions,
  correlation score bounds/symmetry/monotonicity, authorization invariants.
- External providers are replaced with in-process fakes in tests. Provider adapters get contract
  tests against recorded fixtures; live calls only in an opt-in category (`[Trait("Category","Live")]`).
- Integration tests use real PostgreSQL (not SQLite/InMemory) so constraints and `timestamptz` behave as in production.

## Critical properties (each must have at least one named test)

1. Unauthorised users cannot perform restricted operations (per-endpoint role matrix).
2. AI recommendations cannot alter `ApprovedSeverity`.
3. Duplicate reports are never deleted or hidden by correlation.
4. An unverified location is never returned with `isVerified = true`.
5. An assignment cannot be acknowledged by a responder other than the assignee.
6. Incident status transitions follow the defined table (property test over all pairs).
7. Every state-changing command writes an audit record in the same transaction.
8. Failed AI analysis leaves the report and incident intact and the analysis retryable.
9. Anonymous callers receive 401 from every non-public endpoint.
10. Resolved/closed incidents retain their full history.

## Frontend

- Vitest + React Testing Library for components and hooks; MSW for API mocking in component tests.
- `fast-check` for any pure client-side logic with invariants (e.g. filter composition).
- Playwright e2e against the real API + PostgreSQL for the demonstration scenario (design.md §12).

## Commands

```bash
# backend (Phantom_API)
dotnet build PHANTOM.sln
dotnet test --project tests/PHANTOM.UnitTests
dotnet test --project tests/PHANTOM.IntegrationTests   # requires Docker running
# (global.json opts into Microsoft.Testing.Platform, required by xUnit v3 on .NET 10)

# frontend (Phantom_UI)
npm run lint && npm run typecheck && npm test -- --run
PHANTOM_DEMO_PASSWORD=<seeded demo password> npx playwright test
```
