---
inclusion: always
---

# PHANTOM — Product Steering

PHANTOM is an AI-assisted incident intelligence and response coordination platform. It turns
fragmented public reports into structured, traceable incident workflows for an emergency
coordination organisation.

Canonical requirements: `.kiro/specs/phantom-platform/requirements.md` (this repository,
`Phantom_API`). The frontend repository (`Phantom_UI`) follows these; it does not redefine them.

## Non-negotiable product principles

1. **Human authority.** PHANTOM is a decision-support tool, not an autonomous dispatch system.
   AI never dispatches services, diagnoses, changes approved severity, assigns responders,
   or resolves incidents. Every operational decision is made by an authorised human and is
   attributable to them in the audit log.
2. **AI output is a recommendation, stored apart from approved values.** `AiRecommendedSeverity`
   and `ApprovedSeverity` are different fields with different write paths. Only a coordinator
   action writes the approved value.
3. **Nothing is silently lost.** Original reports are immutable. Duplicates are suggested,
   never discarded or auto-merged. Failed AI analysis leaves the report intact and retryable.
4. **Honest data.** Never invent coordinates, statistics, or integration results. An
   approximate or unverified location is labelled as such everywhere it appears. Simulated or
   demonstration data is labelled as simulated.
5. **Everything significant is audited.** Actor, action, entity, before/after, timestamp.

## Users

| Role | Authenticated | Core purpose |
|---|---|---|
| Administrator | Yes | Users, roles, responders, settings, audit, reports |
| Incident Coordinator | Yes | Review AI assessments, verify, prioritise, link duplicates, assign, resolve |
| Responder | Yes | Acknowledge/decline assignments, update progress |
| Public Reporter | **No** | Submit reports (text, location, images, voice) and receive a reference |

## Priority

- **P0** must work: auth & roles, public reporting, dashboard, incident list/detail, AI
  classification & recommendations, human verification, map, duplicate detection, responder
  assignment, status updates, audit trail.
- **P1**: voice transcription, image analysis, weather, SignalR real-time, advanced reports.
- **P2 (deferred)**: GPS tracking, SMS/WhatsApp, native mobile, multi-agency federation,
  predictive analytics, autonomous agent orchestration.

Never trade P0 completeness for P1/P2 features.

## Language and claims

- Say "recommended", "suggested", "possible duplicate", "approximate location" — not
  "detected", "confirmed", "exact" — unless a human has confirmed it.
- Do not describe the system as certified, compliant, or production-approved.
- Weather is context, never evidence of an emergency or a prediction of outcome.
