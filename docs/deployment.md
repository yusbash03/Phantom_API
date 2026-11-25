# Deploying PHANTOM

The target layout follows design §13:

| Part | Where | Notes |
|---|---|---|
| SPA | Vercel | `Phantom_UI/vercel.json`: `/api/*` rewrite to the API, SPA fallback, CSP and headers |
| API | Render web service (Docker, Frankfurt) | `Phantom_API/render.yaml` and `Dockerfile` |
| Database | Neon PostgreSQL (Frankfurt, aws-eu-central-1) | Use the **direct** (non `-pooler`) host. EF migrations take session-level locks. |
| Files | Supabase Storage (S3 API, eu-west-1 Ireland) | `Storage__Provider=S3`. Render's disk is wiped on every deploy, so Production refuses local storage. |
| Live updates | Browser → API directly | `VITE_HUB_URL`. Vercel rewrites cannot carry WebSockets. |

Everything below was verified locally with the production image on 2026-11-23:
- the migration bundle ran as the owner role;
- the roles script applied, and the runtime role was refused UPDATE, DELETE and DISABLE TRIGGER on `audit_logs`;
- `/health/ready` returned 200 and unknown hosts got 400;
- a photo upload was stored in S3;
- the API's security headers were present.

The full Playwright suite was also run against the production SPA build with the Vercel headers (`vite preview`).
**Nothing has been deployed yet.** These steps need the owner's accounts.

## 1. Database (Neon)

1. Create a **separate** project (or at least a separate database) for production. Never reuse the development database, which is full of test data. Choose region **AWS Europe Central 1 (Frankfurt)**, the same city as the API (`render.yaml`: `frankfurt`) and close to Supabase Storage (eu-west-1, Ireland). Database calls happen on almost every request, so the API and the database must share a region. Create database `phantom`.
2. Apply the migrations once, either from the image (`docker run --rm --entrypoint ./efbundle phantom-api --connection "<owner>"`) or through the first Render deploy's pre-deploy step.
3. Create the runtime role (security review B-F3):
   `psql "<owner>" -v app_password="'<strong-random>'" -f docs/deployment/db-roles.sql`
4. The runtime connection string uses `Username=phantom_app`. This is `ConnectionStrings__Phantom`.

## 2. Object storage (Supabase Storage)

1. In the Supabase project, Storage → **New bucket**, e.g. `phantom-attachments`. Leave **Public bucket off**: downloads always go through the API's authorised endpoint.
2. Project Settings → Storage → **S3 Connection**: enable it, note the **Endpoint** and **Region**, and create an **S3 access key**. Copy the secret when it is shown.
3. Values:
   - `Storage__S3__ServiceUrl=https://<project-ref>.supabase.co/storage/v1/s3`
   - `Storage__S3__Region=<project region, e.g. us-east-2>`
   - `Storage__S3__Bucket=phantom-attachments`
   - `Storage__S3__AccessKeyId` and `Storage__S3__SecretAccessKey` from the key you created.

Any other S3-compatible service works the same way (Cloudflare R2: endpoint `https://<account-id>.r2.cloudflarestorage.com`, region `auto`).

## 3. API (Render)

1. New → Blueprint → this repository. `render.yaml` defines the service:
   - a Docker build;
   - health check `/health/ready`;
   - a pre-deploy step `./efbundle --connection "$MIGRATIONS_CONNECTION_STRING"`, which needs a paid instance type.
2. Fill in the `sync: false` secrets when prompted:

   | Variable | Value |
   |---|---|
   | `ConnectionStrings__Phantom` | runtime role (`phantom_app`) connection string |
   | `MIGRATIONS_CONNECTION_STRING` | owner connection string (release step only) |
   | `Jwt__SigningKey` | ≥ 32 random bytes, e.g. `openssl rand -base64 48` |
   | `Ai__ApiKey` | Gemini key. Without it, analyses fail honestly and can be retried. |
   | `Mapbox__AccessToken` | server-side geocoding token |
   | `Storage__S3__*` | from step 2 |
   | `Cors__AllowedOrigins__0` | the Vercel site origin, e.g. `https://phantom.vercel.app` |
   | `AllowedHosts` | the API host name, e.g. `phantom-api.onrender.com` |

3. `ForwardedHeaders__TrustPlatformProxy=true` is preset (security review B-F4). It is safe only because Render serves the service exclusively through its proxy, so rate limits see real client IPs.
4. Optional: `Privacy__IpHashKey` (a separate key for reporter-IP pseudonyms) and `Auth__*` limits (see `.env.example`).
5. Demo data for judging (task 38, Requirement 20) is off by default. See "Judging deployment" below.

Check: `curl https://<api-host>/health/ready` should return `200`.

## 4. SPA (Vercel)

1. Import `Phantom_UI`. The framework is Vite and the output is `dist` (from `vercel.json`).
2. If the API host is not `phantom-api.onrender.com`, edit `vercel.json` in **both** places: the `/api` rewrite and the CSP `connect-src` (`https://…` and `wss://…`).
3. Environment variables:
   - `VITE_MAPBOX_PUBLIC_TOKEN`: a **URL-restricted** public token (restrict it to the Vercel domain in the Mapbox dashboard).
   - `VITE_HUB_URL=https://<api-host>/hubs/operations`.

Check: open the site, submit a public report, sign in, and confirm the top bar says "Live".

## Judging deployment (demo data and recorded fixtures)

For a public judging instance only, set all three. Leave them unset for any real use.

| Variable | Effect |
|---|---|
| `Seed__Enabled=true` and `Seed__AllowDemoInProduction=true` | Seeds three demo accounts (`admin@`, `coordinator@` and `responder@phantom.demo`), five demo responders and five demo incidents around Lagos. Every record is flagged `IsDemoData`, shown with a "Demo data" badge, and each incident title starts with "Demo:". Seeding runs once. |
| `Seed__DemoUserPassword` | The demo accounts' password (at least 12 characters). Share it only with the judges. |

Only with both seed switches on, a live provider may be replaced by its labelled recorded fixture (Requirement 20.3).
In any other Production deployment the API refuses to start with a fixture.

| Variable | Effect |
|---|---|
| `Ai__Provider=RecordedFixture` | Replays one real gemini-3.8-flash response, shown as "Recorded test fixture — not an analysis of this report". Use it if no Gemini key is available. |
| `Weather__Provider=Fixture` | Replays one real Open-Meteo reading, shown as "Recorded test fixture — not live weather". Use it if Open-Meteo is unavailable. |

## 5. After deploying

- Visit `/health/ready` publicly; that completes task 37.
- Make sure no proxy, CDN or log drain records full request URLs for `/hubs/operations` (security review F-L3). SignalR sends the access token as `access_token` in the WebSocket URL; the API itself never logs query strings.
- Don't publish Playwright traces from any CI run (F-I4); they contain the demo login.
- Secrets live only in Render and Vercel. Rotate `Jwt__SigningKey` to sign everyone out.

## Rollback

Render keeps previous deploys: roll back from the dashboard. Migrations are forward-only. If a release must
be undone after a migration ran, deploy a fix forward; do not edit the migrations history by hand.
