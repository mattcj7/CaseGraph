# Ticket Index

This file tracks planned, active, and completed tickets.

## How to use
- The single source of truth for ticket execution is this file.
- Codex executes only the ticket listed in **Active Ticket** with scope defined in **Active Ticket Spec**.
- Every ticket pass must update:
  - **Upcoming Tickets** (current and deduplicated)
  - **Completed Tickets** (append-only)

## Active Ticket
- ID: (none)
- Title: (none queued)

## Active Ticket Spec
```md
# Ticket: T0014 - Link message participants to Targets/Identifiers (v1)

## Goal
Make it fast to turn comms data into usable People/Targets intel by letting the user:
1) Link a message participant (phone/email/handle) to an existing Target
2) Create a new Target from a message participant and link the identifier automatically
3) Record audit + provenance so links are defensible and explainable

## Context
We now have:
- Messages ingest + search
- Targets + identifier CRUD
Next step is bridging them: investigators should be able to click a sender/recipient shown in message results and attach it to a known person/target.

## Scope

### A) UI: “Link to Target…” from message participants
Add UI actions wherever message participants are shown (minimum MVP surfaces):
- Search results that show message hits with participants
- Message thread / message detail view (if present)

For each participant display (sender/recipient/participants list), add:
- Context menu or inline button: **Link to Target…**
- Context menu or inline button: **Create Target from this…**

### B) Link flow (modal dialog)
Implement a small modal:
- Shows the selected participant value (raw + normalized if available)
- Lets user:
  1) Select existing Target (searchable dropdown/list)
  2) OR create a new Target (name field prefilled from participant where reasonable)
- Shows conflict state if identifier already linked to another target (see D)

### C) Service: link identifier with provenance
Implement an application service method (or reuse existing TargetRegistryService methods) to:
- Normalize participant value (reuse IdentifierValueGuard / normalizers)
- Resolve identifier type (at minimum: Phone, Email, Handle/Account; if type cannot be inferred, user must choose)
- Upsert the Identifier record and link to the selected Target
- Persist provenance for the link as evidence-derived analyst action:
  - SourceEvidenceItemId = the message event’s EvidenceItem (or the containing evidence item id)
  - SourceLocator = locator for the message event / row
  - IngestModuleVersion = current module version (or “UI-Link-v1” if you separate)
- Write an AuditLog entry for:
  - LinkIdentifierToTarget (include targetId, identifier type/value, source locator)
  - CreateTargetFromParticipant (if applicable)

### D) Conflict handling (no silent merges)
If the identifier is already linked to a different target:
- Show a conflict UI with options:
  - Cancel (default)
  - Keep existing link and also add as non-primary to selected target (only if allowed)
  - Reassign identifier to selected target (requires confirmation + audit note)
No auto-resolution. User must pick.

### E) Tests
Add tests using real SQLite provider:
1) Linking from message participant creates expected TargetIdentifierLink and audit entry:
   - Seed case + a message event with known participant + provenance fields
   - Link participant to target
   - Assert:
     - Identifier record exists
     - Link exists to target
     - Audit entry exists and includes source locator/evidence id
2) Conflict test:
   - Identifier already linked to Target A
   - Attempt link to Target B
   - Assert conflict path requires explicit resolution (service returns a conflict result or throws a controlled ConflictException handled by UI)

### F) Non-goals (explicit)
- No fuzzy matching / auto-merging targets
- No bulk “link all participants automatically”
- No social account graphing yet (just identifiers → targets)

## Acceptance Criteria
- [ ] From a message participant display, user can click “Link to Target…” and complete the link.
- [ ] User can “Create Target from this…” and it creates the target + links identifier.
- [ ] Conflicts are surfaced and require explicit user choice (no silent merges).
- [ ] Audit log entries are written for link/create actions.
- [ ] Provenance is attached to the link (source evidence item + locator).
- [ ] `pwsh tools/test-smart.ps1` passes (DB tier should run for these changes).

## Manual Verification
1) Open a case with imported messages.
2) Search for a known number/handle.
3) On a hit, use “Link to Target…” → select an existing target.
4) Confirm the target’s Identifiers list shows the linked identifier with source/provenance.
5) Use “Create Target from this…” on another participant; confirm it appears in People/Targets.
6) Try linking an identifier already linked to a different target; verify conflict UI requires explicit choice.

## Codex Instructions
- Implement ONLY this ticket scope.
- Use `pwsh tools/test-smart.ps1` for verification (DB tier expected).
- Do NOT run root `dotnet test` unless FULL_TESTS_REQUIRED.
- Update `Docs/TICKETS.md` appropriately (move to Completed only after tests + verify steps).
- End with summary + files changed + commands run + results.


## Upcoming Tickets
- (none currently queued)

## Completed Tickets (append-only)
- 2026-02-12 - T0002 - Established WPF solution skeleton, app shell, MVVM, and DI baseline.
- 2026-02-12 - T0003 - Implemented case workspace, immutable evidence vault import, manifests, and integrity verification UI/tests.
- 2026-02-13 - T0004 - Added SQLite workspace persistence for cases/evidence, append-only audit logging, and recent activity UI.
- 2026-02-13 - T0005 - Added a persistent SQLite-backed job queue with background runner, UI job progress/cancel/history, and queue execution tests.
- 2026-02-13 - T0006 - Added message ingest (UFDR/XLSX best-effort), SQLite message schema + FTS search, queue integration, and working Search UI with provenance.
- 2026-02-13 - T0006 - Added migration-first workspace DB initialization with legacy backup/rebuild repair and startup error visibility to prevent no-window startup failures.
- 2026-02-13 - T0006 - Added startup bootstrap file logging, required-table repair with workspace.broken backups, and startup/runner DB init gating verification.
- 2026-02-14 - T0007 - Completed message ingest/search v1 hardening with platform-filtered cancellable search, deterministic XLSX threading, UFDR/XLSX guidance statuses, and deterministic tests.
- 2026-02-14 - T0008 - Added live parse progress/status UX, queued/running cancel reliability, sender/recipient search filters, and unified app/job debug logging with deterministic tests.
- 2026-02-14 - T0008A - Fixed terminal job overwrite semantics, race-safe cancel handling, and fresh no-tracking UI job reads so completed jobs show 100% and final status immediately.
- 2026-02-15 - T0008B - Enforced final non-throttled terminal overwrite in runner, with cancellation-safe save and deterministic success/cancel/failure finalize tests.
- 2026-02-15 - T0008C - Added Evidence Drawer live MessagesIngest job syncing with fresh no-tracking refresh, terminal-aware cancel visibility, and manual refresh control.
- 2026-02-15 - T0009 - Added People/Targets v1 schema, UI workflows, message participant linking, explicit conflict handling, audits, and deterministic tests.
- 2026-02-15 - T0009A - Fixed missing spacing resource startup crash and hardened no-sheet MessagesIngest terminal guidance/progress behavior.
- 2026-02-15 - T0009A - Stabilized app launch exception visibility, terminal-safe/monotonic job progress writes, verify-job dedupe, and immutable-vault hash verification regressions.
- 2026-02-17 - T0009B - Moved latest/recent job reads into a SQLite-safe infrastructure job query service with integration coverage, and added correlation-aware UI exception logging across global handlers.
- 2026-02-18 - T0009B - Added SQLite-safe case/audit query services for Open Case flows, switched VM reads to those services, and added UseSqlite integration coverage for case/evidence/audit ordering.
- 2026-02-18 - T0009C - Hardened workspace open/create with migration-on-open, fatal crash diagnostics UX, diagnostics page, self-test CLI, and old-DB smoke coverage.
- 2026-02-19 - T0009D - Added safe async command containment for key flows, workspace initializer migration/query verification logging, and SQLite regression tests for empty-db initialization.
- 2026-02-19 - T0010 - Added structured JSON logging scopes/correlation, safe async action containment, debug bundle export UX, observability tests, and repository guardrail docs/scripts.
- 2026-02-19 - T0010A - Replaced live workspace.db reads with SQLite snapshot export in debug bundles, added in-use DB coverage, and surfaced export failure guidance.
- 2026-02-19 - T0010B - Added WER LocalDumps toggle, durable bounded session journal with clean-exit detection breadcrumbs, and debug bundle inclusion for dumps/session artifacts with abstraction-based tests.
- 2026-02-20 - T0010C - Added Diagnostics crash-dumps toggle + open dumps folder UX, WER LocalDumps ExpandString folder defaults (mini dumps, count 10), and bounded dump inclusion with registry/debug-bundle tests.
- 2026-02-20 - T0010D - Added tiered test runner scripts (fast/db/ui/full/smart), routed smart verification from git diff triggers, and codified verification policy/docs.
- 2026-02-20 - T0011 - Confirmed Diagnostics crash-dump toggle/folder UX, HKCU WER LocalDumps defaults, bounded dump bundle export, and abstraction-based registry coverage.
- 2026-02-20 - T0012 - Removed cross-thread UI access from crash context capture via session state, contained unobserved task exceptions, and added forget/context/unobserved regression tests.
- 2026-02-20 - T0012A - Fixed SQLite-unsafe DateTimeOffset ordering in target list queries by applying in-memory sort post-materialization and added SQLite coverage for no-throw + expected ordering.
- 2026-02-21 - T0012C - Added startup hang guards (busy timeout, watchdog timeout, single-instance mutex), expanded workspace init step logging, and validated target ordering/timeout behavior with tests.
- 2026-02-21 - T0012D - Expanded smart DB-tier triggers with path+diff-keyword reasons and `-ForceDb`, updated AGENTS DB-tier policy, and deflaked MessagesIngest audit test by executing queued jobs deterministically in-test.
- 2026-02-21 - T0013 - Added identifier input validation UX, routed Add Identifier through safe async handling, switched service validation to ArgumentException, and added guard/service regression tests.
- 2026-02-21 - T0014 - Added participant-to-target link/create modal actions in Search, explicit conflict resolution (including non-primary dual-link), UI-Link-v1 provenance/audit logging, and SQLite conflict/link coverage.
