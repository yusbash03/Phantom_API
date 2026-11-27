# Deploying PHANTOM

The target layout follows design §13:

| Part | Where | Notes |
|---|---|---|
| SPA | Vercel | `Phantom_UI/vercel.json`: `/api/*` rewrite to the API, SPA fallback, CSP and headers |
| API | Render web service (Docker, Frankfurt, **free plan**) | `Phantom_API/render.yaml` and `Dockerfile`. Starter plan kept commented out in `render.yaml`. |
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
**Status (2026-11-27):** the API is deployed at `https://phantom-api-xnkr.onrender.com` (free plan, using the
development database for now), with its reference at `/scalar`. The SPA is at `https://phantom-ui-two.vercel.app`.

## 1. Database (Neon)

1. Create a **separate** project (or at least a separate database) for production. Never reuse the development database, which is full of test data. Choose region **AWS Europe Central 1 (Frankfurt)**, the same city as the API (`render.yaml`: `frankfurt`) and close to Supabase Storage (eu-west-1, Ireland). Database calls happen on almost every request, so the API and the database must share a region. Create database `phantom`.
2. Apply the migrations with the owner connection string (see "Applying migrations" below).
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
   - the **free** instance type. It sleeps after about 15 minutes without traffic and cannot run a pre-deploy
     step, so migrations are applied by hand (below). See "Keeping the free instance awake".
2. Fill in the `sync: false` secrets when prompted:

   | Variable | Value |
   |---|---|
   | `ConnectionStrings__Phantom` | runtime role (`phantom_app`) connection string |
   | `Jwt__SigningKey` | ≥ 32 random bytes, e.g. `openssl rand -base64 48` |
   | `Ai__ApiKey` | Gemini key. Without it, analyses fail honestly and can be retried. |
   | `Mapbox__AccessToken` | server-side geocoding token |
   | `Storage__S3__*` | from step 2 |
   | `Cors__AllowedOrigins__0` | the Vercel site origin: `https://phantom-ui-two.vercel.app` (no trailing slash) |
   | `AllowedHosts` | the API host name: `phantom-api-xnkr.onrender.com` |

3. `ForwardedHeaders__TrustPlatformProxy=true` is preset (security review B-F4). It is safe only because Render serves the service exclusively through its proxy, so rate limits see real client IPs.
4. Optional: `Privacy__IpHashKey` (a separate key for reporter-IP pseudonyms) and `Auth__*` limits (see `.env.example`).
5. Demo data for judging (task 38, Requirement 20) is off by default. See "Judging deployment" below.

Check: `curl https://<api-host>/health/ready` should return `200`.

### Applying migrations (free plan)

Before deploying a version that adds migrations, apply them from a workstation with the **owner** connection string.
The app keeps running as `phantom_app`, so the audit-log protection is unaffected. If a migration only adds things, the
old version keeps working against the new schema until Render switches over.

```bash
# from the repository root, using the same image Render builds
docker build -t phantom-api .
docker run --rm --entrypoint ./efbundle phantom-api --connection "<owner connection string>"
# or, without Docker (needs `dotnet tool install --global dotnet-ef`):
dotnet ef database update --project src/PHANTOM.Infrastructure --startup-project src/PHANTOM.API --connection "<owner connection string>"
```

Don't set `Database__MigrateOnStartup=true` instead: it would need the owner role at run time, which lets the app
alter the audit log.

### Keeping the free instance awake

A free Render instance sleeps after about 15 minutes without requests; the next request waits roughly a minute, and
the background workers (AI analysis, geocoding, weather) pause while it sleeps. An external monitor that calls the API
every 5–10 minutes keeps it awake:

- **[UptimeRobot](https://uptimerobot.com)** (free): New monitor → HTTP(s) → `https://<api-host>/health/live`,
  interval 5 minutes. It also emails you when the API is down.
- Alternative: **[cron-job.org](https://cron-job.org)** (free), a GET to the same URL every 10 minutes.

Ping `/health/live`, not `/health/ready`: it doesn't query the database, so Neon's compute can still suspend when
nobody is using the app. One always-on free service uses about 730 of the 750 free instance hours Render gives a
workspace each month, so keep only **one** free service awake. The Vercel site is static and never sleeps.

### Switching to the Starter plan later

In `render.yaml`, replace `plan: free` with `plan: starter`, uncomment `preDeployCommand` and the
`MIGRATIONS_CONNECTION_STRING` variable, then set that variable (owner connection string) in Render. Migrations then
run automatically before each deploy, the instance never sleeps, and the keep-awake monitor can be removed.

## 4. SPA (Vercel)

1. Import `Phantom_UI`. The framework is Vite and the output is `dist` (from `vercel.json`).
2. `vercel.json` points at `phantom-api-xnkr.onrender.com`. If the API host changes, edit `vercel.json` in **both** places: the `/api` rewrite and the CSP `connect-src` (`https://…` and `wss://…`).
3. Environment variables:
   - `VITE_MAPBOX_PUBLIC_TOKEN`: a **URL-restricted** public token (restrict it to the Vercel domain in the Mapbox dashboard).
   - `VITE_HUB_URL=https://phantom-api-xnkr.onrender.com/hubs/operations`.

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
