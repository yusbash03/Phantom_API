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
