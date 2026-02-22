# Ticket Index

This file tracks planned, active, and completed tickets.

## How to use
- The single source of truth for ticket execution is this file.
- Codex executes only the ticket listed in **Active Ticket** with scope defined in **Active Ticket Spec**.
- Every ticket pass must update:
  - **Upcoming Tickets** (current and deduplicated)
  - **Completed Tickets** (append-only)

## Active Ticket
- T0016 - SQLite lock hardening for Job Queue + Cancel + OpenCase/Workspace init

### T0016 - SQLite lock hardening for Job Queue + Cancel + OpenCase/Workspace init

#### Goal
Eliminate user-facing crashes and “hung” behavior caused by SQLite write-lock contention by making JobQueue writes, Cancel operations, and workspace init resilient (retry/backoff, throttling, and non-fatal UI handling).

#### Context / Evidence
We have confirmed fatal UI-thread crashes caused by `SQLite Error 5: 'database is locked'` during:
- `JobQueueService.CancelAsync(...)` triggered by `CancelCurrentOperationAsync()` (UI command crash path).
- `ParseMessages` command failing due to locked DB during job/audit writes.
Additionally, OpenCase/Workspace init can time out when the DB is locked.

#### In Scope
1) JobQueueService write resiliency
- Add a focused retry/backoff policy for SQLITE_BUSY/SQLITE_LOCKED (Error 5 / locked) around ALL JobQueue write operations:
  - Enqueue / Start / ReportProgress / Complete / Fail / Cancel
- Retries MUST be bounded and logged (attempt count, delay, final failure).
- If a write still fails after retries, surface a non-fatal error to UI (toast/dialog) and keep the app running.

2) Progress write throttling
- Throttle JobQueue progress persistence so we do not write to SQLite for every tiny increment.
- Keep UI responsiveness: UI can still update live via in-memory progress events; database persistence can be coarser (e.g., max 2-4 writes/sec OR “only on percent change”).
- Ensure final persisted progress always ends at 1.0 (100%) on success.

3) Cancel command safety
- Ensure CancelCurrentOperationCommand is executed via the SafeAsyncActionRunner pattern so Cancel failures do not crash the UI thread.
- If cancel cannot be persisted immediately due to lock, show “Cancel requested…” and continue retrying briefly; if it still fails, show a clear error with next steps.

4) Workspace init / OpenCase non-fatal handling
- If workspace init/migration verification times out due to lock, do NOT hard-exit the entire app.
- Show a user-readable error with:
  - workspace db path
  - logs path
  - likely cause (DB locked by a running job or another instance)
  - a “Retry” action.

#### Out of Scope
- Big architecture changes (separate DB for job queue, moving off SQLite, major refactor of ingest pipeline).
- New UI pages/features beyond minimal messaging/toast/dialog updates needed to avoid crashes.

#### Acceptance Criteria
- Repro: Start a long MessagesIngest; while it runs, click Cancel.
  - Expected: no crash; Cancel either succeeds or shows a non-fatal message and retries.
- Repro: Start ParseMessages while DB is temporarily locked (simulated or real contention).
  - Expected: no crash; operation fails gracefully with actionable message OR waits/retries then proceeds.
- Job history: succeeded jobs persist Progress=1.0 and final status text is stable (no regressions where progress stays < 1.0).
- OpenCase/Workspace init: lock-induced timeout produces a non-fatal UI error with retry; app stays open.
- Automated tests added to prevent regressions (see Test Plan).

#### Test Plan (Automated)
- Add infrastructure test that simulates SQLite write lock:
  - Open a SqliteConnection to workspace.db (or a temp db), begin an IMMEDIATE transaction to hold the write lock.
  - Call JobQueueService.CancelAsync and/or ReportProgressAsync.
  - Assert: method does not throw; returns controlled failure result (or exception is wrapped/handled); logs contain retry attempts.
- Add test for progress throttling behavior (e.g., multiple calls within short interval only persist limited writes).
- Update/extend any existing JobQueueService tests as needed.

#### Manual Verify Steps
1) Run app, parse 10k XLSX.
2) While parsing, click Cancel.
3) Confirm:
   - UI stays responsive, no crash.
   - Cancel either completes or clearly reports failure without exiting app.
4) Open/close cases and confirm OpenCase doesn’t kill the app on lock; shows retry UI if needed.

#### Notes / Implementation Guidance
- Prefer a small shared helper (e.g., SqliteWriteRetry) used only where needed (JobQueue writes).
- Log retries with correlationId and operation name.
- Avoid increasing global timeouts as the main fix; prefer retry + throttling + correct UI containment.


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
- 2026-02-22 - T0015 - Added startup-only workspace migration gating with cached success, introduced workspace write gate + SQLite busy/locked retry/backoff on progress/audit/provenance write paths, hardened non-disruptive SafeReportProgress behavior, and added deterministic lock-resilience runner/gate tests.
- 2026-02-22 - T0016 - Hardened JobQueue writes (enqueue/start/progress/finalize/cancel) with bounded SQLite lock retries + structured retry/exhaustion logging, added progress persistence throttling with live in-memory updates, moved Cancel to safe async containment with lock-aware user guidance, and made workspace-init lock timeouts non-fatal with retryable startup messaging.
