# Configuration

Settings use standard ASP.NET Core configuration. In `appsettings*.json` they appear as `Section:Key`; as environment
variables, use `Section__Key` (double underscore), e.g. `Ai__ApiKey`. **Secrets** come only from environment
variables or `dotnet user-secrets`, never from source; `.env.example` holds placeholders only.

Options are validated at startup: an out-of-range value stops the API with a message naming the setting.

## Required

| Setting | Secret | Default | Notes |
|---|---|---|---|
| `ConnectionStrings:Phantom` | yes | — | PostgreSQL (Npgsql format). On Neon, use the **direct** host, not the `-pooler` one. In production this is the restricted runtime role (`docs/deployment/db-roles.sql`). |
| `Jwt:SigningKey` | yes | — | At least 32 bytes (HS256), e.g. `openssl rand -base64 48`. Changing it signs everyone out. |

## Authentication and limits

| Setting | Default | Notes |
|---|---|---|
| `Jwt:Issuer`, `Jwt:Audience` | `phantom-api`, `phantom-ui` | |
| `Jwt:AccessTokenLifetimeMinutes` | 15 | |
| `Auth:RefreshTokenLifetimeDays` | 7 (1–30) | Each refresh rotates the token. |
| `Auth:AbsoluteSessionLifetimeDays` | 30 (1–90, ≥ refresh lifetime) | No session lasts longer, however often it refreshes. |
| `Auth:MaxFailedLoginsPerAccount` | 10 | Failures per account **per client** within the window before that client is refused. |
| `Auth:FailedLoginWindowMinutes` | 15 | |
| `RateLimiting:PublicPermitsPerMinute` | 20 | Per client IP, for public report submission and status lookups. |
| `RateLimiting:LoginPermitsPerMinute` | 10 (100 in Development) | Per client IP. |
| `RateLimiting:RefreshPermitsPerMinute` | 60 | Per client IP. |

## Hosting

| Setting | Default | Notes |
|---|---|---|
| `Cors:AllowedOrigins` | `[]` (`http://localhost:5173` in Development) | Exact origins only; credentials allowed; no wildcard. |
| `AllowedHosts` | `*` | Set to the API's host name in production. |
| `ForwardedHeaders:TrustPlatformProxy` | false | true **only** behind a platform proxy that is the sole way in (e.g. Render), so rate limits see real client IPs. |
| `Database:MigrateOnStartup` | false (true in Development) | Production runs migrations as a release step (`efbundle`). |
| `OpenApi:ExposeUi` | false | Swagger UI and `/openapi/v1.json` outside Development. |
| `Privacy:IpHashKey` | derived from `Jwt:SigningKey` | Secret. Key for the HMAC of reporter IPs kept for abuse analysis. |

## File storage

| Setting | Default | Notes |
|---|---|---|
| `Storage:Provider` | `Local` | `Local` or `S3`. **Production requires `S3`**: container disks are wiped on every deploy. |
| `Storage:Local:BasePath` | system temp `/phantom-storage` | Development only; outside the web root. |
| `Storage:S3:ServiceUrl` | — | Supabase Storage: `https://<project-ref>.supabase.co/storage/v1/s3`. Cloudflare R2: `https://<account-id>.r2.cloudflarestorage.com`. Leave empty for AWS S3. |
| `Storage:S3:Bucket` | — | Keep the bucket private; downloads go through the API. |
| `Storage:S3:Region` | `auto` | Supabase: the project's region (e.g. `us-east-2`), which must match. R2: `auto`. |
| `Storage:S3:AccessKeyId`, `Storage:S3:SecretAccessKey` | — | Secrets. Use a token scoped to this one bucket. |

## AI (Gemini)

| Setting | Default | Notes |
|---|---|---|
| `Ai:Provider` | `Gemini` | `Gemini`, or `RecordedFixture` (a labelled replay of one real response) in Development, Testing, Demo, or an explicit judging deployment. |
| `Ai:ApiKey` | — | Secret. Without it: a labelled **simulated** AI in Development; elsewhere analyses fail honestly with "not configured" and can be retried. |
| `Ai:Model` | `gemini-3.8-flash` | |
| `Ai:TimeoutSeconds` | 60 (1–120) | Per attempt. |
| `Ai:MaxRetries` | 2 (0–5) | Retries on 429 and 5xx, with jitter. |
| `Ai:Temperature`, `Ai:MaxOutputTokens` | 0.2, 8192 | |
| `Ai:TranscriptionEnabled` | true | Voice notes are transcribed through Gemini audio input (only with the Gemini provider and a key). |
| `Ai:WorkerEnabled` | true | Background analysis worker. |
| `Ai:PollIntervalSeconds`, `Ai:StuckAfterMinutes` | 10, 5 | Queue polling, and recovery of crashed claims. |

## Geocoding (Mapbox)

| Setting | Default | Notes |
|---|---|---|
| `Mapbox:AccessToken` | — | Secret, server-side. Without it, no coordinates are invented: locations stay unplotted until a coordinator verifies them. |
| `Mapbox:CountryCodes` | — | e.g. `ng` to bias results. |
| `Mapbox:Language`, `Mapbox:TimeoutSeconds` | `en`, 8 | |
| `Mapbox:WorkerEnabled`, `Mapbox:PollIntervalSeconds`, `Mapbox:StuckAfterMinutes` | true, 10, 5 | |

The browser map uses a **separate, URL-restricted public token** (`VITE_MAPBOX_PUBLIC_TOKEN` in Phantom_UI).

## Weather (Open-Meteo)

| Setting | Default | Notes |
|---|---|---|
| `Weather:Provider` | `OpenMeteo` | Or `Fixture` (a labelled replay of one real reading), on the same terms as the AI fixture. |
| `Weather:TimeoutSeconds` | 5 | |
| `Weather:CacheMinutes` | 15 | Default; administrators can change it in Settings. 0 disables the cache. |
| `Weather:WorkerEnabled`, `Weather:PollIntervalSeconds`, `Weather:RetryFailedAfterMinutes` | true, 15, 5 | |

## Duplicate detection

`Correlation:GeoWeight` 0.35, `TimeWeight` 0.15, `CategoryWeight` 0.15, `TextWeight` 0.20, `LocationWeight` 0.15,
`GeoRadiusMeters` 1500, `WindowHours` 6, `SuggestThreshold` 0.55, `MaxCandidates` 100. These are the defaults;
administrators can change all except `MaxCandidates` at run time (**Settings**, audited, effective within 30 s).

## Demo data

| Setting | Default | Notes |
|---|---|---|
| `Seed:Enabled` | false (true in Development) | Seeds labelled demo accounts, responders and incidents, once. |
| `Seed:AllowDemoInProduction` | false | Explicit opt-in for a judging deployment only. With `Seed:Enabled`, it also allows the recorded fixtures. |
| `Seed:DemoUserPassword` | — | Secret; at least 12 characters; shared by the demo accounts. |
