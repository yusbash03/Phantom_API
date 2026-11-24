# Demo script

A ten-minute walkthrough of the demonstration scenario (design §12). Every step below is exercised automatically by
`Phantom_UI/e2e/demo-scenario.spec.ts`, so if that test passes, the demo works.

## Before you start

- Use an instance with **demo data** (Development, or a judging deployment; see `deployment.md`). The demo accounts
  are `admin@phantom.demo`, `coordinator@phantom.demo` and `responder@phantom.demo`, with the configured demo password.
- Open three browser windows:
  1. a private window on `/report` (the public);
  2. the coordinator, signed in, on the **Dashboard**;
  3. the responder, signed in, on **My assignments**. A phone-sized window works well here.
- Check the top bar says **Live**. If it says "Live updates paused", screens still refresh every minute.
- If Gemini or Open-Meteo is unavailable, the instance may use **recorded fixtures**. They are labelled on screen
  ("Recorded test fixture — not an analysis of this report" / "… not live weather"). Say so; don't present them as live.

## 1. A member of the public reports a fire (Intake, Req. 4)

In the private window, fill in the report: *"Sparks and smoke from the pole transformer on the corner"*, category
**Fire**, a location description, and optionally coordinates, a photo or a voice note. Submit.

- Point out the **reference** (e.g. `PH-2026-000123`). No account is needed. "Check status" shows only a coarse status.
- Photos are re-encoded on the server so their location metadata is stripped. Uploads are checked by content, not by name.

## 2. It appears for the coordinator immediately (Real time, Req. 13; Dashboard, Req. 12)

In the coordinator window, the **Dashboard** counts and **Recent activity** update without a reload. Open the incident.

## 3. AI structures and assesses the report (Intelligence, Req. 6)

The **AI recommendation** panel fills in within seconds:
- summary, recommended severity and category, hazards, missing information and suggested next actions;
- a footer naming the model and whether the text, photos or voice transcript were used.

- It is marked **"Advisory only"**. The **approved** severity stays "Not yet assessed": AI never decides.
- If the AI fails, the report is unchanged and a coordinator can retry.

## 4. It is on the map, honestly labelled (Geocoding and map, Req. 8)

Open the **Operations map**. The marker's style and label say how the location was obtained: reported by the public,
geocoded (approximate), AI-inferred, or verified by a coordinator. The page also says how many incidents cannot be
plotted. The **Weather** card on the incident shows conditions near the location, marked as context only.

## 5. A second report of the same fire (Correlation, Req. 7)

Submit a second, similar report a few metres away. On it, PHANTOM shows a **possible duplicate** with a match
score and an explanation (distance, time, category, wording, place names).

- Confirm the duplicate, keeping the first as primary. The second incident closes, and its report moves to the first
  one; **nothing is deleted**. "Mark unrelated" would stop it being suggested again.

## 6. Review and verify (Incidents, Req. 5)

On the first incident: **Start review**, then **Verify**, choosing an approved severity. Picking one different from the
AI's shows that the coordinator decides. Verifying the location changes its label to "Verified".

## 7. Assign a responder (Assignments, Req. 11)

Choose **Assign responder** and pick MED-01. Availability and skills are shown; assigning someone busy needs a reason.
The incident becomes **Assigned**.

## 8. The responder acknowledges and reports progress (Req. 11)

In the responder window, the assignment and a **notification** have already arrived. Then:
1. **Acknowledge**.
2. **On my way**: the incident moves to In progress automatically, with the reason recorded.
3. **Post update**.
4. **Arrived on scene**.
5. **Complete**, with resolution notes.

## 9. Resolve (Req. 5.7)

Back in the coordinator window, the progress is visible. **Resolve**, adding a summary. Only a coordinator can resolve.

## 10. The audit trail (Req. 15)

Scroll to **History** on the incident. It tells the whole story in order, in plain language, saying who did what. AI
entries are marked **AI** and automatic changes are marked **System**, with their reason. As the administrator, open
**Audit log** to filter every recorded action, with before and after values. Records cannot be changed or deleted,
and the database enforces this.

## Optional extras

- **Reports**: period counts, median response times, and how often the AI's severity matched the coordinator's, with
  a CSV download.
- **Settings**: duplicate-detection weights and the weather cache, validated and audited.
- **Accessibility**: everything above works by keyboard and at phone width.

## What to say about limits

- PHANTOM recommends; people decide. It is not certified or approved for real emergency use.
- Demo records carry a **"Demo data"** badge, and seeded incident titles start with "Demo:".
- There is no live responder tracking; the map shows incident locations only.
