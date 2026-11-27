# PHANTOM API reference

Base path `/api/v1`. JSON throughout, with enums as strings. The machine-readable contract is
[`contracts/openapi.json`](../contracts/openapi.json), generated at build time. The interactive reference is Scalar at `/scalar` (Swagger UI at `/swagger`), in
Development or when `OpenApi:ExposeUi=true`. The deployed API serves it at
<https://phantom-api-xnkr.onrender.com/scalar>.

## Authentication

- `POST /auth/login` returns a **15-minute JWT access token** in the body. A **refresh token** is set as an
  `HttpOnly; Secure; SameSite=Strict; Path=/api/v1/auth` cookie and is never returned in the body.
- Send `Authorization: Bearer <access token>`. On 401, call `POST /auth/refresh` (the cookie is rotated) and retry once.
- Refresh tokens rotate on every use. Reusing a rotated token revokes the whole session family. A session cannot be
  extended beyond `Auth:AbsoluteSessionLifetimeDays` (30 days).
- Roles are re-read from the database on every refresh. Deactivated users cannot refresh.

## Access levels

| Level | Who |
|---|---|
| Public | Anyone, no token. The public endpoints are rate-limited per client IP. |
| Signed in | Any authenticated user. A global fallback policy applies to every endpoint not marked public. |
| Staff | Administrator or Incident Coordinator |
| Coordinator | Incident Coordinator (operational decisions) |
| Administrator | Administrator |
| Responder | Responder; assignment actions only on the caller's own assignments |

Resources the caller may not see return **404**, not 403, so their existence is not revealed.

## Errors

Every error is RFC 9457 `application/problem+json` with `title`, `status`, a stable `code` and a `traceId`.
Validation failures (400) add field-level `errors`. Notable statuses:
- `409 Conflict`: a stale `version` or `expectedUpdatedAt` (optimistic concurrency); reload and retry.
- `422`: a business rule (e.g. assigning a busy responder without an override reason).
- `429`: a rate limit.

Provider error bodies and stack traces are never returned.

## Concurrency

Incident, assignment and responder commands take the `version` the caller last read. Settings take
`expectedUpdatedAt`. A mismatch is a 409, so nobody overwrites a change they have not seen.

## Endpoints

### Public reporting

| Method | Path | Access | Notes |
|---|---|---|---|
| POST | `/api/v1/public/reports` | Public (rate-limited) | Submits a public incident report and returns its reference. |
| GET | `/api/v1/public/reports/{reference}/status` | Public (rate-limited) | Returns the coarse public status of a submitted report. |

### Auth

| Method | Path | Access | Notes |
|---|---|---|---|
| POST | `/api/v1/auth/login` | Public | Signs in. The refresh token is set as an HttpOnly cookie and never returned in the body. |
| POST | `/api/v1/auth/refresh` | Public (refresh cookie) | Rotates the refresh cookie and returns a new access token. |
| POST | `/api/v1/auth/logout` | Public (refresh cookie) | Revokes the session's refresh-token family and clears the cookie. |
| GET | `/api/v1/auth/me` | Signed in |  |

### Incidents

| Method | Path | Access | Notes |
|---|---|---|---|
| GET | `/api/v1/incidents` | Staff |  |
| GET | `/api/v1/incidents/map` | Staff | Incidents with coordinates for the operations map, plus a count of those that cannot be plotted. |
| GET | `/api/v1/incidents/{id}` | Staff |  |
| PATCH | `/api/v1/incidents/{id}` | Coordinator | Edits the working title, description and category. The original public reports are never changed. |
| POST | `/api/v1/incidents/{id}/review` | Coordinator |  |
| POST | `/api/v1/incidents/{id}/verify` | Coordinator | Verifies the incident. An approved severity is required and is recorded as the coordinator's decision. |
| POST | `/api/v1/incidents/{id}/reject` | Coordinator |  |
| POST | `/api/v1/incidents/{id}/resolve` | Coordinator |  |
| POST | `/api/v1/incidents/{id}/reopen` | Coordinator |  |
| POST | `/api/v1/incidents/{id}/close` | Coordinator |  |
| PUT | `/api/v1/incidents/{id}/severity` | Coordinator | Changes the approved severity of a verified incident. AI recommendations never use this path. |
| PUT | `/api/v1/incidents/{id}/location` | Coordinator | Confirms or corrects the location. This is the only way a location becomes verified. |

### AI analyses

| Method | Path | Access | Notes |
|---|---|---|---|
| GET | `/api/v1/incidents/{incidentId}/analyses` | Staff | All attempts, newest first. |
| POST | `/api/v1/incidents/{incidentId}/analyses/retry` | Staff | Queues a new attempt when the latest one failed (Requirement 6.5). |

### Duplicates and links

| Method | Path | Access | Notes |
|---|---|---|---|
| GET | `/api/v1/incidents/{incidentId}/relations` | Staff |  |
| POST | `/api/v1/incidents/{incidentId}/relations` | Coordinator | Links another incident (by number) as Related without closing either. |
| PUT | `/api/v1/relations/{id}` | Coordinator | Confirm a duplicate (requires the primary), mark related, or mark unrelated (never re-suggested). |

### Weather

| Method | Path | Access | Notes |
|---|---|---|---|
| GET | `/api/v1/incidents/{incidentId}/environment` | Staff | The latest reading with its source and retrieval time, or why there is none. |
| POST | `/api/v1/incidents/{incidentId}/environment/refresh` | Staff | Retrieve again now. Readings are cached per area for a short period, so this may return the cached one. |

### Assignments

| Method | Path | Access | Notes |
|---|---|---|---|
| POST | `/api/v1/incidents/{incidentId}/assignments` | Coordinator |  |
| GET | `/api/v1/incidents/{incidentId}/assignments` | Staff |  |
| POST | `/api/v1/assignments/{id}/cancel` | Coordinator |  |
| GET | `/api/v1/assignments/mine` | Responder | The signed-in responder's assignments, active first. |
| GET | `/api/v1/assignments/{id}` | Signed in (staff or the assignee) | Staff, or the assigned responder; anyone else gets 404. |
| POST | `/api/v1/assignments/{id}/acknowledge` | Responder (assignee) | Acknowledgement is acceptance (decision D6). |
| POST | `/api/v1/assignments/{id}/decline` | Responder (assignee) |  |
| POST | `/api/v1/assignments/{id}/status` | Responder (assignee) | Reports the responder EnRoute or OnScene. |
| POST | `/api/v1/assignments/{id}/updates` | Responder (assignee) |  |
| POST | `/api/v1/assignments/{id}/complete` | Responder (assignee) | Completes the assignment with resolution notes. Only a coordinator resolves the incident. |

### Attachments

| Method | Path | Access | Notes |
|---|---|---|---|
| GET | `/api/v1/attachments/{id}/content` | Staff |  |

### Responders

| Method | Path | Access | Notes |
|---|---|---|---|
| GET | `/api/v1/responders` | Staff |  |
| POST | `/api/v1/responders` | Administrator |  |
| GET | `/api/v1/responders/facets` | Staff |  |
| GET | `/api/v1/responders/{id}` | Staff |  |
| PUT | `/api/v1/responders/{id}` | Administrator |  |
| POST | `/api/v1/responders/{id}/deactivate` | Administrator |  |
| POST | `/api/v1/responders/{id}/activate` | Administrator |  |
| PUT | `/api/v1/responders/{id}/availability` | Administrator |  |
| GET | `/api/v1/responders/me` | Responder | The signed-in responder's own profile. |
| PUT | `/api/v1/responders/me/availability` | Responder | The signed-in responder sets their own availability (Available or Unavailable). |

### Notifications

| Method | Path | Access | Notes |
|---|---|---|---|
| GET | `/api/v1/notifications` | Signed in (own) |  |
| POST | `/api/v1/notifications/{id}/read` | Signed in (own) |  |
| POST | `/api/v1/notifications/read-all` | Signed in (own) |  |

### Dashboard

| Method | Path | Access | Notes |
|---|---|---|---|
| GET | `/api/v1/dashboard/summary` | Staff | Headline counts: total, active, high priority, awaiting review, unassigned, unacknowledged, available responders (12.1). |
| GET | `/api/v1/dashboard/activity` | Staff | The most recent incident activity from the audit log, newest first (12.3). |

### Reports

| Method | Path | Access | Notes |
|---|---|---|---|
| GET | `/api/v1/reports/summary` | Staff |  |
| GET | `/api/v1/reports/summary.csv` | Staff | The same summary as CSV: one row per figure (section, item, value, sample_size). |

### Audit

| Method | Path | Access | Notes |
|---|---|---|---|
| GET | `/api/v1/incidents/{incidentId}/timeline` | Staff | The incident's full history: reports, AI analyses, decisions, assignments and updates (15.4, 15.5). |
| GET | `/api/v1/audit` | Administrator | Search by actor, entity, action and date range (15.6). |
| GET | `/api/v1/audit/facets` | Administrator |  |

### Users

| Method | Path | Access | Notes |
|---|---|---|---|
| GET | `/api/v1/users` | Administrator |  |
| POST | `/api/v1/users` | Administrator |  |
| GET | `/api/v1/users/{id}` | Administrator |  |
| PUT | `/api/v1/users/{id}/roles` | Administrator |  |
| POST | `/api/v1/users/{id}/deactivate` | Administrator |  |
| POST | `/api/v1/users/{id}/activate` | Administrator |  |

### Settings

| Method | Path | Access | Notes |
|---|---|---|---|
| GET | `/api/v1/settings` | Administrator |  |
| PUT | `/api/v1/settings` | Administrator | Changes take effect within 30 seconds and are audited with the values before and after. |

## Real-time events (SignalR)

Hub: `/hubs/operations`, authenticated with the access token. Browsers send it as `access_token` on the WebSocket URL,
which is accepted only on this path. Clients cannot invoke anything; the server sends `event` messages:

```json
{ "type": "IncidentStatusChanged", "incidentId": "…", "assignmentId": null, "responderId": null, "at": "2026-11-24T10:00:00Z" }
```

| Type | Sent to |
|---|---|
| IncidentCreated, IncidentUpdated, IncidentStatusChanged, IncidentResolved, AnalysisCompleted, EnvironmentUpdated | staff |
| AssignmentCreated, AssignmentAcknowledged, AssignmentStatusChanged, AssignmentUpdated, ResponderAvailabilityChanged | staff, and the responder concerned |
| NotificationCreated | the notification's recipient only |

Events carry identifiers only and are published **after** the change is committed. Clients refetch through the API
rather than trusting event payloads. A failed write publishes nothing.

## AI output

Analyses follow the `phantom.intel.v1` schema: category, summary, severity and rationale, named locations,
casualty and trapped-person indications, hazards, missing information, recommended next actions, entities and a
confidence explanation. Output is validated strictly before it is stored. It is a recommendation: only the coordinator
endpoints (`verify`, `severity`) set the approved severity. `isSimulated` is true for simulated or recorded-fixture
output, and `usedImageInput` and `usedAudioTranscript` record what the model actually received.
