# Ticket Index

This file tracks planned, active, and completed tickets.

## How to use
- The single source of truth for ticket execution is this file.
- Codex executes only the ticket listed in **Active Ticket** with scope defined in **Active Ticket Spec**.
- Every ticket pass must update:
  - **Upcoming Tickets** (current and deduplicated)
  - **Completed Tickets** (append-only)

## Upcoming Tickets
- (none listed)

## Active Ticket
- (none)

## T0019 - Target-centric Search + “Where Seen” (messages) panel

### Goal
Make Targets immediately useful in investigations by:
1) Showing **Where Seen** (message presence) for the selected Target.
2) Adding **Search filters** so investigators can search messages by Target (via linked identifiers), plus common constraints (identifier type, direction, date range).
3) Providing one-click “Open Search filtered to this Target” from the Target page.

### Context
We now have:
- Messages ingest + search
- People/Targets + identifiers
- Participant linking (T0014) and identifier validation (T0013)
- Workspace write-gate + lock resilience (T0018)

Next is the “investigator workflow bridge”: targets → “where they show up in communications” + filtered searching.

### Scope

#### A) Where Seen panel on Target details
In People/Targets UI, when a Target is selected, add a **Where Seen (Messages)** panel:

Minimum info:
- Total message hit count for this target (across all linked identifiers)
- Breakdown by identifier:
  - Identifier value (display-safe)
  - Identifier type (Phone/Email/Handle/etc.)
  - Count of message events where it appears as sender/recipient/participant
  - “Last seen” timestamp (best-effort, nullable)

Actions:
- Button: **Search messages for this Target**
  - Navigates to Search with Target filter pre-set
- Per-identifier action: **Search for this identifier**

Notes:
- “Where seen” must be computed per-case (current case).
- Keep it fast: counts + last-seen only; no heavy joins beyond what’s needed.

#### B) Search filters v2 (Target-aware)
Extend Search page with filters:

1) Target filter
- Dropdown or searchable selector of Targets in current case.
- When selected, search is constrained to messages where any linked identifier appears in participants.

2) Identifier type filter
- Optional dropdown (Any / Phone / Email / Handle / etc.)
- Applies only when Target filter is set (or when searching by identifier list).

3) Direction filter (optional but recommended)
- Any / Incoming / Outgoing
- Implementation depends on your message schema:
  - If MessageEvent has Sender/Recipient: Incoming = recipient match; Outgoing = sender match
  - If multi-participant: use “sender vs others” semantics where available; otherwise treat as Any.

4) Date/time range (optional but recommended)
- From / To (UTC internally; display local)
- Applies to MessageEvent timestamp field.

UX:
- Filters should not require re-parsing; they query existing tables/FTS.
- Show active filters chips (optional, nice-to-have).

#### C) Query/service changes
Add/extend Infrastructure query services so UI does not do DB logic:

1) Target presence summary query
- Input: caseId, targetId, optional identifierType, optional date range
- Output:
  - totalCount
  - byIdentifier: (identifierId, type, valueDisplay, count, lastSeenUtc?)

2) Message search with Target filter
- If Target filter set:
  - Resolve target identifiers (normalized)
  - Constrain search results to message events whose participant(s) match any identifier
- Preserve existing keyword/body FTS searching; Target filter should combine with it:
  - Example: Target=“John Doe” + keyword=“strap” + date range

Performance guidance:
- Prefer set-based constraints (IN on identifier IDs) over string matching.
- If schema doesn’t have a participant-to-identifier table, implement best-effort matching using stored participant fields, but keep it scoped and indexed where possible.

#### D) Tests (SQLite provider)
Add Infrastructure tests (deterministic, per-temp workspace DB):

1) WhereSeen summary
- Seed:
  - Target + identifiers
  - Messages with participants matching those identifiers
- Assert:
  - totalCount correct
  - per-identifier counts correct
  - lastSeen correct (or null if timestamps missing)

2) Search filter by Target
- Seed:
  - messages for target identifiers and messages for other people
- Assert:
  - target filter returns only matching message events
  - combining with keyword filter still works

3) Optional: Direction filter
- If direction implemented, verify incoming/outgoing separation.

#### E) Ticket tracking + workflow
- Update `Docs/TICKETS.md`:
  - Add T0019 under Upcoming, set Active = T0019.
  - Move T0019 to Completed with date when verified.
- DB tier must run (Infrastructure query changes).

### Acceptance Criteria
- From People/Targets, selecting a Target shows Where Seen counts quickly.
- Clicking “Search messages for this Target” opens Search with filter prefilled and results constrained to the target’s identifiers.
- Search filters (Target/type/direction/date) work together without crashes.
- Tests pass via `pwsh tools/test-smart.ps1 -ForceDb`.

### Manual Verify
1) Open case with messages + at least one linked target identifier.
2) Go People/Targets → select target → confirm Where Seen shows counts.
3) Click “Search messages for this Target” → confirm results match expected.
4) Apply keyword + date range + type filter → confirm results update correctly.

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
- 2026-02-22 - T0015 - Added startup-only workspace migration gating with cached success, introduced workspace write gate + SQLite busy/locked retry/backoff on progress/audit/provenance write paths, hardened non-disruptive SafeReportProgress behavior, and added deterministic lock-resilience runner/gate tests.
- 2026-02-22 - T0016 - Hardened JobQueue writes (enqueue/start/progress/finalize/cancel) with bounded SQLite lock retries + structured retry/exhaustion logging, added progress persistence throttling with live in-memory updates, moved Cancel to safe async containment with lock-aware user guidance, and made workspace-init lock timeouts non-fatal with retryable startup messaging.
- 2026-02-23 - T0017 - Completed jobs reliability hardening: optimistic in-memory cancel state, terminal UI progress normalization to 100%, consistent case-switch blocking with clear reason while cancel is pending, and added lock-resilience + monotonic/terminal progress regression tests.
- 2026-02-23 - T0018 - Added project-wide workspace write policy via `IWorkspaceWriteGate.ExecuteWrite*` + `SqliteBusyRetry`, routed key write paths (jobs, audit/provenance, case/workspace finalize flows) through the policy, added deterministic gate lock/serialization tests, and documented the enforced write rule.
- 2026-02-24 - T0019 - Implemented target-centric search filters (target/type/direction/date), added Where Seen (messages) totals/by-identifier/last-seen panel with target+identifier search actions, and added deterministic SQLite coverage for target presence summary and filtered search combinations.
