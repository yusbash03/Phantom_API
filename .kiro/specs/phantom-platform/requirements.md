# Requirements Document — PHANTOM Platform

## Introduction

PHANTOM (AI-Powered Emergency Intelligence & Response Coordination Platform) receives public
incident reports, enriches them with AI analysis, geospatial and environmental context, suggests
possible duplicates, and supports authorised humans in verifying, prioritising, assigning, and
resolving incidents with a complete audit trail.

PHANTOM is a decision-support tool. It never autonomously dispatches, diagnoses, prioritises,
assigns, or resolves.

Priority tags: **[P0]** must work, **[P1]** strong enhancement, **[P2]** deferred.

## Glossary

- **Report** — an immutable public submission (`IncidentReport`).
- **Incident** — the operational record coordinators manage; one incident has one or more reports.
- **Approved severity** — severity set by a coordinator. **AI-recommended severity** — severity proposed by AI.
- **Location provenance** — one of `Reported`, `AiInferred`, `Geocoded`, `ManuallyVerified`.
- **Operational staff** — Administrator or Incident Coordinator.
- **Severity** — `Low`, `Medium`, `High`, `Critical`.

---

## Requirement 1 — Authentication [P0]

**User Story:** As a staff member, I want to sign in securely, so that only authorised people can access operational data.

#### Acceptance Criteria

1. WHEN a user submits a valid email and password THEN the system SHALL issue a JWT access token (≤ 15 min lifetime) and set a rotating refresh token in an HttpOnly, Secure, SameSite=Strict cookie.
2. WHEN a user submits invalid credentials THEN the system SHALL return a uniform authentication error that does not reveal whether the email exists.
3. WHEN a refresh token is used THEN the system SHALL revoke it, issue a new refresh token in the same family, and issue a new access token with roles re-read from the database.
4. IF a previously rotated refresh token is presented THEN the system SHALL revoke every token in that family and require re-authentication.
5. WHEN a user logs out THEN the system SHALL revoke the active refresh token family and clear the cookie.
6. IF a user account is deactivated THEN the system SHALL reject login and refresh for that account.
7. THE system SHALL rate-limit login attempts per IP address and per email.
8. THE system SHALL store passwords only as salted adaptive hashes.

## Requirement 2 — Roles and authorisation [P0]

**User Story:** As an administrator, I want access enforced by role on the server, so that users can only perform the operations their role allows.

#### Acceptance Criteria

1. THE system SHALL support the roles Administrator, IncidentCoordinator, and Responder for authenticated users, and anonymous Public Reporter access.
2. THE system SHALL deny every API endpoint to unauthenticated callers unless the endpoint is explicitly public.
3. WHEN an authenticated user calls an endpoint their role does not permit THEN the system SHALL return 403.
4. WHEN a responder requests an assignment or incident not linked to them THEN the system SHALL return 404.
5. THE system SHALL permit only Administrators to create users, change roles, deactivate users, and manage application settings.
6. IF an action would leave zero active Administrators THEN the system SHALL reject it.
7. THE system SHALL enforce authorisation for SignalR connections and SHALL only deliver events to groups the user's role permits.

## Requirement 3 — User management [P0]

**User Story:** As an administrator, I want to manage staff accounts, so that the right people have the right access.

#### Acceptance Criteria

1. WHEN an Administrator creates a user THEN the system SHALL require a unique email, display name, at least one role, and an initial password meeting policy (≥ 12 characters).
2. WHEN an Administrator changes a user's roles or active state THEN the system SHALL record an audit event with previous and new values.
3. WHEN a user is deactivated THEN the system SHALL revoke all their refresh tokens.
4. THE system SHALL list and filter users by role and active state.

## Requirement 4 — Public incident reporting [P0; images P1; voice P1]

**User Story:** As a member of the public, I want to report an incident without an account, so that responders learn about it quickly.

#### Acceptance Criteria

1. THE system SHALL accept anonymous submissions containing title, description, category, location description, optional coordinates, optional contact details, optional images, and optional voice recording.
2. WHEN a submission is valid THEN the system SHALL persist the report, create an incident with status `Reported`, return a unique human-readable submission reference (e.g. `PH-2026-000123`), and record the UTC submission time.
3. IF required fields are missing or invalid THEN the system SHALL reject the submission with field-level validation errors and SHALL persist nothing.
4. IF coordinates are provided THEN the system SHALL validate latitude ∈ [-90, 90] and longitude ∈ [-180, 180] and SHALL store them with provenance `Reported` and unverified status.
5. WHEN files are attached THEN the system SHALL accept only allow-listed types verified by content signature, SHALL enforce per-file and per-report size and count limits, and SHALL reject the whole submission if any file fails.
6. WHEN an image is stored THEN the system SHALL strip embedded metadata.
7. WHEN a submission repeats an `Idempotency-Key` seen within 24 hours THEN the system SHALL return the original reference and SHALL NOT create a second report.
8. THE system SHALL rate-limit public submissions per client IP.
9. THE public reporting interface SHALL NOT expose internal user, incident, or management data.
10. WHEN a reporter queries their reference THEN the system SHALL return only a coarse public status (Received, Under review, Being handled, Closed).

## Requirement 5 — Incident management [P0]

**User Story:** As a coordinator, I want a structured incident record with a controlled lifecycle, so that every incident is handled consistently.

#### Acceptance Criteria

1. THE system SHALL support categories Fire, MedicalEmergency, RoadAccident, Security, Flood, Infrastructure, Other.
2. THE system SHALL support statuses Reported, UnderReview, Verified, Assigned, InProgress, Resolved, Closed, Rejected.
3. THE system SHALL permit only these transitions:

   | From | To | Actor |
   |---|---|---|
   | Reported | UnderReview, Rejected, Closed (duplicate only) | Coordinator |
   | UnderReview | Verified, Rejected, Closed (duplicate only) | Coordinator |
   | Verified | Assigned | System, on first active assignment |
   | Verified | Resolved | Coordinator (with resolution) |
   | Assigned | InProgress | System, when an assignee goes EnRoute |
   | Assigned | Verified | System, when all assignments end without completion |
   | Assigned, InProgress | Resolved | Coordinator (with resolution) |
   | Resolved | Closed | Coordinator |
   | Resolved | InProgress | Coordinator (reopen, with reason) |

   Closed and Rejected are terminal.
4. IF a transition is not in the table THEN the system SHALL reject it with a typed error and SHALL NOT change state.
5. WHEN an incident moves to Verified THEN the system SHALL require an approved severity and SHALL record the verifying coordinator.
6. WHEN an incident moves to Rejected or Closed-as-duplicate THEN the system SHALL require a reason.
7. WHEN an incident moves to Resolved THEN the system SHALL require resolution details.
8. THE system SHALL store incident number, title, description, category, approved severity, AI-recommended severity, location, reporter details where provided, created/updated timestamps (UTC), assigned coordinator, and resolution details.
9. WHEN a coordinator edits incident title, description, or category THEN the system SHALL keep the original report content unchanged and SHALL audit the previous and new values.
10. THE system SHALL detect concurrent conflicting updates to an incident and SHALL reject the later one with a conflict error.
11. THE system SHALL provide a paginated, filterable (status, category, severity, date range, assigned/unassigned, text search) and sortable incident list.

## Requirement 6 — AI incident intelligence [P0 text; P1 image & voice]

**User Story:** As a coordinator, I want AI to structure and assess each report, so that I can triage faster — while I keep the decision.

#### Acceptance Criteria

1. WHEN a report is persisted THEN the system SHALL create an `IncidentAnalysis` with status `Pending` in the same transaction and SHALL process it asynchronously.
2. THE system SHALL request from the AI provider a structured result containing: category, summary, severity, severity rationale, named locations, potential casualties, possible trapped persons, hazards, missing information, recommended next actions, extracted entities, and confidence explanation.
3. WHEN the provider responds THEN the system SHALL validate the response against schema version `phantom.intel.v1` (types, enum values, string length bounds) before saving.
4. IF the response fails validation, times out, or errors THEN the system SHALL mark the analysis `Failed` with a sanitised failure reason, SHALL leave the report and incident unchanged, and SHALL allow manual review.
5. WHEN an analysis has failed THEN an Operational staff user SHALL be able to request a retry, which creates a new analysis attempt.
6. THE system SHALL record provider, model identifier, analysis timestamp, input report reference, schema version, processing status, and latency for each analysis.
7. WHEN an analysis succeeds THEN the system SHALL update only the incident's AI-recommended severity and AI-recommended category fields, and SHALL NEVER modify approved severity, status, assignments, or resolution.
8. THE system SHALL NOT include reporter contact details, IP addresses, or user identifiers in AI prompts.
9. THE system SHALL access the LLM only through `IIncidentIntelligenceService` so that the provider can be replaced without changing application logic.
10. [P1] WHERE an image is attached THE system SHALL include it in analysis and record that image input was used.
11. [P1] WHERE a voice recording is attached and transcription is configured THE system SHALL transcribe it, store the transcript as a derived artefact linked to the original recording, and include it in analysis.
12. THE UI SHALL label AI output as "AI recommendation" and display it separately from approved values.

## Requirement 7 — Incident correlation (duplicate detection) [P0]

**User Story:** As a coordinator, I want possible duplicate reports suggested with an explanation, so that I can consolidate them without losing information.

#### Acceptance Criteria

1. WHEN a new incident is created (and again after its AI analysis completes) THEN the system SHALL compare it with incidents created within a configurable time window (default 6 h) that are not Closed or Rejected.
2. THE system SHALL compute a deterministic score in [0, 1] from weighted signals: geographic distance, time difference, category match, description similarity, and overlapping extracted location entities. Weights and thresholds SHALL be configurable.
3. WHEN the score meets the suggestion threshold (default 0.55) THEN the system SHALL record a `PossibleDuplicate` relation with the score, the matching signals, and a human-readable explanation.
4. THE system SHALL NOT delete, hide, merge, or change the status of any incident or report as a result of correlation.
5. WHEN a coordinator confirms a duplicate THEN the system SHALL associate the secondary incident's reports with the primary incident, close the secondary incident with reason "Duplicate of {number}", and preserve all original report records.
6. WHEN a coordinator marks a pair as unrelated THEN the system SHALL record that decision and SHALL NOT re-suggest the same pair.
7. THE system SHALL allow a coordinator to manually link two incidents as `Related` without closing either.
8. THE correlation score SHALL be symmetric, bounded to [0, 1], and SHALL not decrease when any single signal improves with others held constant.

## Requirement 8 — Geospatial intelligence [P0]

**User Story:** As a coordinator, I want incidents on a map with honest location confidence, so that I understand where things are happening.

#### Acceptance Criteria

1. THE system SHALL store incident location as text plus optional latitude/longitude (numeric(9,6)), provenance, precision, and verification state (verified by, verified at).
2. WHEN an incident has a location description and no reported coordinates THEN the system SHALL attempt server-side geocoding and, on success, store coordinates with provenance `Geocoded` and unverified state.
3. IF geocoding fails or returns no result THEN the system SHALL keep the text location, store no coordinates, and flag the incident as "location needs verification".
4. THE system SHALL NOT fabricate coordinates; AI-extracted place names SHALL only produce coordinates via the geocoder and SHALL carry provenance `AiInferred`.
5. WHEN a coordinator confirms or corrects a location THEN the system SHALL set provenance `ManuallyVerified`, record the verifier and timestamp, and audit the previous value.
6. THE system SHALL return `isVerified = true` only when provenance is `ManuallyVerified`.
7. THE operations map SHALL display incidents with category and severity, allow selecting an incident, and filter by category, severity, and status.
8. THE map SHALL visually distinguish verified from unverified locations and show provenance in the incident popup.
9. THE system SHALL NOT display responder positions (no live tracking source exists) [P2].

## Requirement 9 — Environmental context [P1]

**User Story:** As a coordinator, I want relevant weather context for an incident, so that I can anticipate conditions.

#### Acceptance Criteria

1. WHEN an incident has coordinates THEN the system SHALL retrieve current temperature, precipitation, wind speed, wind direction, and gusts from the weather provider.
2. THE system SHALL store each retrieval as a snapshot with source, retrieval timestamp, and coordinates used.
3. THE system SHALL emphasise category-relevant fields (wind for Fire; precipitation for Flood; precipitation/visibility for RoadAccident).
4. THE system SHALL cache retrievals per rounded coordinate for a configurable period (default 15 min) and SHALL apply a request timeout (default 5 s).
5. IF the provider fails THEN the system SHALL show "Weather unavailable" with the reason category and SHALL not block any incident workflow.
6. THE UI SHALL display source and retrieval time and SHALL NOT present weather as evidence or prediction.

## Requirement 10 — Responder management [P0]

**User Story:** As an administrator, I want to maintain responder profiles, so that coordinators can assign the right people.

#### Acceptance Criteria

1. THE system SHALL store responder name, unique call sign/identifier, team, contact details, skills, availability, active assignment, last status update time, and an optional linked user account.
2. WHEN an Administrator creates, updates, or deactivates a responder THEN the system SHALL validate the data and audit the change.
3. THE system SHALL list responders with filters for team, skill, availability, and active state.
4. THE system SHALL derive availability `Assigned` from active assignments and SHALL allow the responder or an Administrator to set `Available` / `Unavailable` when no assignment is active.

## Requirement 11 — Assignment workflow [P0]

**User Story:** As a coordinator, I want to assign responders and track their progress, so that I know who is handling what.

#### Acceptance Criteria

1. WHEN a coordinator assigns an available, active responder to a Verified, Assigned, or InProgress incident THEN the system SHALL create an assignment with status `Assigned` and notify the responder.
2. IF the responder is inactive or already has an active assignment THEN the system SHALL reject the assignment unless the coordinator explicitly overrides with a reason (audited).
3. THE assignment status SHALL follow: Assigned → Acknowledged → EnRoute → OnScene → Completed; Assigned or Acknowledged → Declined (reason required); any non-terminal → Cancelled (coordinator, reason required).
4. WHEN a responder acknowledges THEN the system SHALL treat it as acceptance and record the acknowledgement time.
5. IF a user who is not the linked user of the assigned responder attempts to acknowledge, decline, or update the assignment THEN the system SHALL reject it (404).
6. WHEN a responder submits a progress update THEN the system SHALL store it as an `IncidentUpdate` linked to the assignment.
7. THE system SHALL never create, change, or cancel an assignment automatically from AI output.
8. WHEN a responder completes an assignment THEN the system SHALL store their resolution notes; only a coordinator SHALL resolve the incident.

## Requirement 12 — Command dashboard [P0]

**User Story:** As operational staff, I want a live command view, so that I can see the current picture at a glance.

#### Acceptance Criteria

1. THE dashboard SHALL show total incidents, active incidents (not Resolved/Closed/Rejected), high-priority active incidents (approved severity High or Critical), unverified incidents awaiting review, unassigned verified incidents, assignments awaiting acknowledgement, and available responders.
2. THE system SHALL compute every metric from database records at request time; the UI SHALL contain no static metric values.
3. THE dashboard SHALL show recent activity (from the audit log) and geographic distribution (map).

## Requirement 13 — Real-time updates [P1]

**User Story:** As operational staff, I want updates to appear without refreshing, so that I react quickly.

#### Acceptance Criteria

1. THE system SHALL publish SignalR events for: incident created, incident status changed, analysis completed, assignment created, assignment acknowledged, assignment status changed, responder availability changed, incident resolved.
2. WHEN an event is received THE frontend SHALL invalidate the affected TanStack Query caches rather than patching data from the event payload.
3. IF the real-time connection drops THEN the frontend SHALL show a "Live updates paused" indicator, attempt reconnection with backoff, and fall back to refetch-on-focus and a 60-second refetch interval on dashboard queries until reconnected.
4. Event payloads SHALL contain identifiers and minimal display data only, and SHALL be delivered only to authorised groups (staff; or the specific responder for their assignments).

## Requirement 14 — Notifications [P0 in-app]

**User Story:** As a user, I want in-app notifications for things that need my attention.

#### Acceptance Criteria

1. THE system SHALL create a `Notification` for: a responder when assigned or when an assignment is cancelled; coordinators when a Critical AI recommendation, a possible duplicate, a decline, or an assignment completion occurs.
2. THE system SHALL let a user list their notifications, see an unread count, and mark notifications read.
3. A user SHALL only access their own notifications.

## Requirement 15 — Incident history and audit [P0]

**User Story:** As an administrator or coordinator, I want a complete, tamper-resistant history, so that every decision is accountable.

#### Acceptance Criteria

1. WHEN any of these occur THEN the system SHALL write an audit record in the same database transaction: incident creation, report attachment, AI analysis completion/failure/retry, verification, severity change, status change, field edit, location verification, duplicate suggestion, link/unlink/unrelated decision, assignment create/cancel/status change, responder progress update, resolution, reopen, closure, user/role/responder changes, settings changes, login success/failure.
2. EACH audit record SHALL contain actor (user ID, or `system`, or `anonymous`), action, entity type, entity ID, UTC timestamp, previous value and new value (JSON) where applicable, correlation ID, and metadata.
3. THE system SHALL NOT expose any API to update or delete audit records, and the database SHALL reject UPDATE and DELETE on the audit table.
4. THE incident detail view SHALL show a unified timeline of reports, AI analyses, human decisions, assignments, and updates, with AI events visibly distinguished from human decisions.
5. WHEN an incident is Resolved or Closed THEN its full history SHALL remain readable.
6. Administrators SHALL be able to search the audit log by actor, entity, action, and date range.

## Requirement 16 — Reporting [P1]

**User Story:** As an administrator, I want summary reports, so that I can review performance.

#### Acceptance Criteria

1. THE system SHALL provide, for a date range: incidents by category, severity, and status; median time from report to verification, to first acknowledgement, and to resolution; AI recommendation vs approved severity agreement rate.
2. THE system SHALL compute reports from database records and SHALL allow CSV export.

## Requirement 17 — Application settings [P1]

**User Story:** As an administrator, I want to adjust operational parameters without redeploying.

#### Acceptance Criteria

1. THE system SHALL allow Administrators to view and change correlation weights and thresholds, correlation time window, and weather cache duration, with validation of ranges.
2. THE system SHALL audit every settings change.
3. THE system SHALL NOT expose secrets through the settings API.

## Requirement 18 — Frontend experience [P0]

**User Story:** As any user, I want a clear, professional interface that works on desktop and mobile.

#### Acceptance Criteria

1. THE frontend SHALL provide: Login, Command dashboard, Incident list, Incident detail (with intelligence panel and timeline), Public report form, Operations map, Responders, Assignments ("My assignments" for responders), Notifications, Audit/history, Reports, Users & roles, Settings.
2. THE navigation SHALL show only screens the user's role permits, while the backend remains the enforcement point.
3. EVERY data view SHALL implement loading, error, and empty states.
4. EVERY interactive control SHALL perform a working action or be absent.
5. Severity SHALL be conveyed by icon and text as well as colour.
6. The layout SHALL be usable from 360 px wide upward; the public report form SHALL be mobile-first.
7. Destructive or high-impact actions (reject, close, cancel assignment, confirm duplicate, deactivate) SHALL require a confirmation dialog.

## Requirement 19 — Security and operations [P0]

**User Story:** As the platform owner, I want sensible security and operational controls.

#### Acceptance Criteria

1. THE system SHALL return RFC 9457 ProblemDetails with a correlation ID and SHALL NOT expose stack traces or provider error bodies outside Development.
2. THE system SHALL restrict CORS to configured origins.
3. THE system SHALL read all secrets from environment configuration; the repositories SHALL contain only placeholder examples.
4. THE system SHALL expose `/health/live` and `/health/ready` (database connectivity).
5. THE system SHALL apply security headers (HSTS in production, `X-Content-Type-Options`, `Referrer-Policy`, frame denial).
6. THE system SHALL log structured events with correlation IDs without logging secrets or personal contact data.

## Requirement 20 — Demonstration data [P0]

**User Story:** As a presenter, I want a reproducible demo scenario without misleading anyone.

#### Acceptance Criteria

1. WHEN the application starts in Development or the `Demo` environment with seeding enabled THEN the system SHALL seed demo users (one per role), responders, and sample incidents marked `IsDemoData = true`.
2. THE system SHALL NOT seed demo data when the environment is Production unless `Seed__AllowDemoInProduction=true` is explicitly set for the judging deployment, and SHALL then label every demo record in the UI with a "Demo data" badge.
3. WHERE an external provider is unavailable during a demo THE system SHALL use only previously retrieved, labelled fixture data, shown with a "Recorded fixture" label — never presented as live.
