# Security review — task 35 (2026-11-22)

Two read-only reviews, one per repository, followed the project's own reviewer brief
(`.kiro/agents/phantom-security-reviewer.json`) against `.kiro/steering/security.md`. Every finding was then checked
against the code before it was fixed. This records what was checked and what was changed. It is **not** a claim that
PHANTOM is secure, certified or compliant.

Dependency checks on 2026-11-22: `dotnet list package --vulnerable --include-transitive` found no vulnerable packages,
and `npm audit` (production and dev) found 0 vulnerabilities.

## Fixed

| # | Finding | Severity | Fix | Tests |
|---|---|---|---|---|
| B-F1 | Audio was accepted on a 2–4 byte signature and stored as uploaded. Downloads used the reporter's file name, so a `.html` or `.exe` "recording" could be saved by a coordinator. | Medium | Downloads use a server-generated name with the extension of the sniffed type (`phantom-recording-…​.webm`), in the API and in the SPA's save. Audio is checked for structure: MP3 needs two valid frames after an optional well-formed ID3 tag, WebM must declare DocType `webm`, and MP4 rejects image brands (HEIC/AVIF). The whole file is inspected, not 16 bytes. | `MagicByteFileInspectorTests`, `SecurityFixesTests`, `VoiceTests`, e2e voice |
| B-F2 | Anyone could keep a staff account locked out: failures were counted per account, and throttled attempts counted too. | Medium | `LoginThrottle` counts failures per (account, client IP), and refused attempts are not counted. One client can lock out only its own attempts. Every failure is still audited. | `SecurityFixesTests`, `AuthTests` |
| B-F5 | Parallel anonymous image uploads could use excessive memory. | Low | Pixel cap lowered from 40 MP to 24 MP; at most two decodes run at once. | existing intake tests |
| B-F6 | Reporter IP "hash" used a public constant salt, so it could be reversed. | Low | HMAC-SHA256 with `Privacy:IpHashKey`, or a key derived by HKDF from `Jwt:SigningKey`. | `SecurityFixesTests` |
| B-F8 | Two administrators could demote or deactivate each other at the same moment and leave none. | Low | Role changes and deactivations run inside a transaction holding a PostgreSQL advisory lock. | existing user tests |
| B-F9 | Refreshing could keep a session alive indefinitely. | Low | Refresh is refused once the token family is older than `Auth:AbsoluteSessionLifetimeDays` (default 30). | `AuthTests` |
| B-F10 | Open-Meteo request URLs, which contain rounded coordinates, reached the logs. | Low | HttpClient loggers removed for that client, as for Mapbox. | — |
| B-F11 | Only the exact `<report>`/`</report>` delimiters were neutralised in prompts. | Info | Regex covers spaced and decorated variants. | `GeminiIntelligenceServiceTests` |
| B-F13 | A very large page number overflowed into a negative OFFSET (500 error). | Info | Page clamped to 100,000. | `SecurityFixesTests` |
| B-F14 | `.env.example` and the steering doc named `Mapbox__SecretToken`; the code reads `Mapbox__AccessToken`. | Info | Docs corrected; new optional settings documented. | — |
| F-L1 | A refresh running at sign-out could put a fresh access token back in memory. | Low | A session generation counter: sign-out disowns any in-flight refresh, and late results are discarded. | `session.test.ts` |
| F-L2 | Reporter phone and email went into `tel:`/`mailto:` links unchecked, allowing extra mail headers such as `?bcc=`. | Low | Links only for strictly valid values; anything else is shown as text. The API's email check stays permissive, so the UI guard is the control. | `contact-links.test.ts` |
| F-I1 | The live-updates connection retried refresh forever after the session ended. | Info | A failed refresh there ends the session. | — |
| F-I2 | UI components imported a short, generic npm package `cn` alongside the identical local helper. | Info | Imports moved to `@/lib/utils`; package removed. | build, Vitest |

## Open: deployment configuration (task 37)

- **B-F3: audit-log immutability depends on the DB role.** The trigger rejects UPDATE/DELETE/TRUNCATE, but if the app role owns `audit_logs` it can disable the trigger. In production, run migrations as a separate owner role and grant the runtime role only `INSERT, SELECT` on `audit_logs` (and the needed rights elsewhere).
- **B-F4: rate limiting behind the load balancer.** Set `ForwardedHeaders:TrustPlatformProxy=true` only where the app is reachable solely through the platform proxy, so limits see real client IPs and cannot be bypassed by spoofed `X-Forwarded-For`.
- **F-L3: hub tokens in proxy logs.** Browsers send the SignalR token in the WebSocket URL. The API never logs query strings, but the hosting proxy and CDN must not log `access_token` either.
- **F-I3: Content-Security-Policy for the SPA.** The API already sends strict headers. The static host (Vercel) needs a CSP allowing self, the API origin, and `api.mapbox.com`/`events.mapbox.com` with `worker-src blob:`, plus `frame-ancestors 'none'`, nosniff and a referrer policy.
- **F-I4: Playwright traces.** If CI is added, do not publish failure traces; they contain the demo login.
- `AllowedHosts` should be set to the real host names in production.

## Accepted (documented residual risk)

- **B-F7: public status references are sequential** (`PH-2026-000123`, per the design). They reveal report volume and a coarse status (Received / Under review / Being handled / Closed), and the endpoint is rate-limited. A separate random tracking token would change the reporter-facing design; recommended for a later version.
- **B-F12: access tokens stay valid for up to 15 minutes after deactivation or a role change.** Refresh tokens are revoked at once, and the hub closes connections when their token expires.
- **Spoken or typed names** in report text and transcripts can reach the AI. Phone numbers and emails are removed from transcripts only. The steering doc allows report text in prompts.
- **Login throttling is in-memory**, which suits a single API instance. A scaled-out deployment needs a shared store.
