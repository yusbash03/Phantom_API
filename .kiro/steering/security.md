---
inclusion: always
---

# PHANTOM — Security Steering

Implementing these controls does not make PHANTOM certified or compliant. Never claim it does.

## Authentication

- Passwords hashed with ASP.NET Core `PasswordHasher<User>` (PBKDF2, versioned format). Minimum length 12.
- Access token: JWT, 15-minute lifetime, HS256 with a ≥256-bit key from configuration
  (`Jwt__SigningKey`), validated issuer, audience, lifetime, signing key; `ClockSkew` ≤ 30 s.
- Refresh token: 256-bit random, stored **hashed** (SHA-256) in `refresh_tokens`, delivered only in an
  `HttpOnly; Secure; SameSite=Strict; Path=/api/v1/auth` cookie. Rotated on every use. Reuse of a
  rotated token revokes the entire token family. Logout revokes the family.
- Frontend keeps the access token in memory only — never `localStorage`/`sessionStorage`.
- Login failures return a uniform error (no user enumeration). Login is rate-limited.

## Authorization

- Enforced on the backend with named policies (`RequireAdministrator`, `RequireCoordinator`,
  `RequireResponder`, `RequireOperationalStaff`). Every controller is `[Authorize]` by default via a
  global fallback policy; public endpoints opt out explicitly with `[AllowAnonymous]`.
- Resource-level checks (IDOR): a responder can read/act on an assignment only when
  `assignment.Responder.UserId == currentUserId`. Load-then-check in the application service, return 404
  (not 403) for resources the caller may not know exist.
- Privilege escalation: only Administrators change roles; an administrator cannot remove the last
  active administrator; role claims are re-read from the DB on refresh, not trusted from the old token.
- SignalR hub requires an authenticated user; group membership is derived server-side from claims.

## Public surface

- Anonymous endpoints: `POST /api/v1/public/reports`, `GET /api/v1/public/reports/{reference}/status`
  (returns only coarse status, no internal data), `GET /health`.
- Rate limiting (built-in `RateLimiter`) on all anonymous endpoints, keyed by client IP after
  `ForwardedHeaders` processing with known proxies only.
- Idempotency: public submissions accept an `Idempotency-Key` header (UUID); duplicate keys within
  24 h return the original submission reference.

## Uploads

- Allow-list by **content sniffing (magic bytes)**, not file extension or client `Content-Type`.
  Images: JPEG, PNG, WebP (≤ 10 MB each, ≤ 5 per report). Audio: WebM/Opus, OGG, MP3, WAV, M4A (≤ 10 MB, ≤ 1).
- Generate server-side storage keys (UUID); never use client file names in paths. Store the original
  name only as sanitised metadata.
- Images are re-encoded server-side to strip metadata (EXIF GPS) before storage.
- Files are never served from a public static path; downloads go through an authorised endpoint
  with `Content-Disposition: attachment` and `X-Content-Type-Options: nosniff`.

## Secrets and configuration

- All secrets via environment variables (`Ai__ApiKey`, `Mapbox__SecretToken`, `Jwt__SigningKey`,
  `ConnectionStrings__Phantom`). Local development uses `dotnet user-secrets` or an untracked `.env`.
- `.env.example` files contain placeholders only. `.gitignore` must exclude `.env*` except `.env.example`.
- The Mapbox token used in the browser is a separate, URL-restricted public token.

## Errors, logging, AI

- Production returns `ProblemDetails` with a correlation ID — no stack traces, no provider error bodies.
- Do not send reporter contact details, IPs, or user identifiers to the LLM. Prompts contain only the
  report text, category, location text, and timestamps.
- Treat all LLM output as untrusted input: parse against the schema, validate, bound string lengths,
  and render as text (never HTML) in the UI.
- Do not log prompts or model responses in full in Production.

## Data layer

- The application DB role has no `UPDATE`/`DELETE` on `audit_logs`; a trigger also rejects them.
- Parameterised queries only (EF Core / `FromSql` interpolation). No string-concatenated SQL.
- CORS: explicit origin allow-list from configuration; no wildcard with credentials.
