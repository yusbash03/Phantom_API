# Kiro Usage Log

A factual record of how Kiro and other tools were used to build PHANTOM. Entries are added as work
happens. Nothing here is written in advance or claimed without evidence.

| Date | Tool / Kiro feature | What was actually done | Evidence |
|---|---|---|---|
| 2026-11-18 | Claude Code (VS Code extension) — **not Kiro** | Inspected both repositories and the toolchain; drafted the initial spec (`requirements.md`, `design.md`, `tasks.md`), five steering documents, five custom agent definitions and five hook definitions in Kiro's file formats | Files under `.kiro/` in both repos (initial uncommitted drafts) |

## Pending verification

These artefacts were authored in Kiro's formats but have **not yet been loaded or exercised in Kiro**:

- Hooks (`*.kiro.hook`): the format was checked against the hook schema embedded in the locally
  installed Kiro 1.4.215. Each hook must be opened in the Kiro IDE's Agent Hooks panel to confirm it
  is recognised and fires as expected.
- Custom agents (`.kiro/agents/*.json`): the format matches the Kiro CLI agents installed under
  `~/.kiro/agents`. Confirm with Kiro CLI that each agent loads.
- Steering front-matter (`inclusion`, `fileMatchPattern`): confirm in the Kiro IDE steering panel.
- Kiro Powers, MCP servers and Kiro Crew: not yet used.

## 2026-11-18 — Phase 2 (Foundation)

| Tool / Kiro feature | What was actually done | Evidence |
|---|---|---|
| Claude Code (VS Code extension) — **not Kiro** | Implemented Phase 2 tasks 1–9, 11, 13, 14 and parts of 10 and 12 from `tasks.md`, following the steering documents | Source in both repos; `tasks.md` notes |
| Spec (`tasks.md`) | Used as the work order; tasks ticked only after tests verified them; partially complete tasks left unticked with notes | `tasks.md` |
| Steering (`security.md`, `testing.md`) | Followed for token handling, cookie flags, rate limiting, audit immutability and the critical-property tests | `AuthService.cs`, migration trigger, `tests/` |
| Testing | Found and fixed two real defects: (1) refresh shared the login rate-limit bucket, locking out users who reload; (2) simultaneous sign-ins of one user hit an optimistic-concurrency conflict | `RateLimitPolicies.cs`, `AuthService.cs`, `AuthTests.Simultaneous_sign_ins_by_the_same_user_all_succeed` |

Kiro hooks, custom agents, MCP, Powers and Kiro Crew were **not** exercised in this phase.

## 2026-11-19 to 2026-11-24 — Phases 3 and 4 (tasks 10 to 38)

| Tool / Kiro feature | What was actually done | Evidence |
|---|---|---|
| Claude Code (VS Code extension), **not Kiro** | Implemented tasks 10 and 12 onwards in both repositories, one task at a time. The maintainer reported that their **Kiro tokens were used up** and asked Claude Code to continue the task list. | Every commit in both repositories (27 in Phantom_API and 25 in Phantom_UI as of 2026-11-24) carries a `Co-Authored-By: Claude` trailer (`git log --format=%B`). |
| Spec (`requirements.md`, `design.md`, `tasks.md`) | Used as the work order and acceptance criteria. Each task was ticked only after its tests passed, with a dated verification note, and partially done work was left unticked (task 37 remains unticked until deployed). | `tasks.md` notes for tasks 10 to 38 |
| Steering (`product.md`, `security.md`, `architecture.md`, `testing.md`, `coding-standards.md`, `frontend.md`) | Followed for the product rules (AI advisory only, honest location labels, nothing deleted, everything audited), security controls, layering and test expectations. | Code and tests; e.g. `IntelligencePanel.tsx` "Advisory only", the `LocationBadge` provenance labels, the audit-log trigger |
| Custom agent `phantom-security-reviewer` (`.kiro/agents/*.json`) | Its prompt was used **as the brief** for two read-only reviewer subagents **in Claude Code** (task 35), one per repository. Each finding was checked in the code before it was fixed. The agent was **not run in Kiro**. | `docs/security-review.md` |
| Hooks (`*.kiro.hook`) | **Not confirmed to run in Kiro.** The checks they describe were run by hand after each task instead: backend tests, contract regeneration (`npm run api:generate`), lint and typecheck. | Verification notes in `tasks.md` |
| Kiro Powers, MCP servers, Kiro Crew | Not used. | — |

### To be completed by the maintainer

- [ ] Which tasks, files or plans (if any) were produced or changed **in Kiro** before the hand-over to Claude Code,
      and on what date the Kiro tokens ran out. No commit in either repository is attributable to Kiro alone, so this
      cannot be established from the repositories and is deliberately left blank rather than guessed.
- [ ] Whether the hooks and custom agents were ever loaded in the Kiro IDE or CLI (see "Pending verification" above).
