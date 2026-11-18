---
inclusion: fileMatch
fileMatchPattern: "**/*.{cs,csproj,ts,tsx}"
---

# PHANTOM — Coding Standards

## C# / .NET 10

- Nullable reference types and `TreatWarningsAsErrors` enabled in every project (`Directory.Build.props`).
- File-scoped namespaces; one public type per file; `sealed` by default for classes not designed for inheritance.
- `async` all the way; pass `CancellationToken` through every async method that does I/O.
- Use `IClock` (Application port) instead of `DateTime.UtcNow` so time is testable.
- Results: application services return `Result<T>` / typed errors for expected failures
  (validation, not found, forbidden, invalid transition). Exceptions are for unexpected failures only.
  The API maps typed errors to RFC 9457 `ProblemDetails`.
- Never swallow exceptions. A `catch` either handles meaningfully (and logs) or rethrows.
- Validation: FluentValidation validators in Application for every request DTO.
- EF Core: configuration via `IEntityTypeConfiguration<T>` classes, never data annotations on
  domain entities. Snake_case table/column names. Use `AsNoTracking()` for reads.
  Use projections (`Select`) to DTOs for list endpoints — no `Include` chains returned wholesale.
- Logging: `ILogger<T>` with structured templates (`"Incident {IncidentId} verified"`), never
  string interpolation. Never log tokens, passwords, contact details, or full report text.
- Options pattern (`IOptions<T>` + `ValidateOnStart`) for all configuration.
- Naming: `XxxService` for application services, `XxxController`, `XxxDto`, `XxxRequest`,
  `XxxResponse`, `XxxConfiguration` (EF), `XxxValidator`.

## TypeScript / React

- `strict: true`, `noUncheckedIndexedAccess: true`. No `any` — use `unknown` and narrow.
- Server state only through TanStack Query hooks in `src/features/<module>/api.ts`.
  Query keys centralised per feature. No server data in global client state.
- Forms: React Hook Form + Zod resolver. Zod schemas validate UX only; the backend is authoritative.
- API types come from `src/api/generated/` (OpenAPI). Do not duplicate them by hand.
- Feature folders: `src/features/<module>/{components,hooks,api.ts,routes.tsx}`.
  Shared UI in `src/components/` (shadcn primitives in `src/components/ui/`).
- Every data view handles loading, error, and empty states explicitly.
- No button without a working handler. No hardcoded statistics.
- Accessibility: every interactive control has an accessible name; severity is never conveyed by colour alone (icon + label).

## General

- Small, verifiable commits; message format `area: imperative summary` (e.g. `incidents: add status transition rules`).
- No secrets in source. `.env.example` / `appsettings.Development.json` contain placeholders only.
- A task in `tasks.md` is ticked only when its acceptance criteria are verified by a passing test or a recorded manual check.
