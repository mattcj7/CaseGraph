# Ticket Index

This file tracks planned, active, and completed tickets.

## How to use
- The single source of truth for ticket execution is this file.
- Codex executes only the ticket listed in **Active Ticket** with scope defined in **Active Ticket Spec**.
- Every ticket pass must update:
  - **Upcoming Tickets** (current and deduplicated)
  - **Completed Tickets** (append-only)

## Active Ticket
- ID: T0009B
- Title: Fix SQLite DateTimeOffset ORDER BY crashes + Add SQLite integration tests for Open Case queries + Improve UI exception logging

## Active Ticket Spec
```md
# Ticket: T0009B - Fix SQLite DateTimeOffset ORDER BY crashes + Add SQLite integration tests for Open Case queries + Improve UI exception logging

## Goal
1) Fix app crash when opening a case caused by EF Core SQLite translation failing on `OrderBy(DateTimeOffset)`.
2) Add SQLite-backed integration tests that exercise the “open case” query paths so these issues are caught automatically.
3) Ensure UI exceptions that show in the error dialog are ALSO written to the rolling log file with a correlation ID.

## Context / Evidence
UI error dialog shows:
- System.NotSupportedException: SQLite does not support expressions of type 'DateTimeOffset' in ORDER BY clauses
Stack trace points to:
- MainWindowViewModel.QueryLatestMessagesParseJobAsync (and subsequent Refresh... calls)

Additionally, some runtime exceptions are not visible in app logs; we need reliable logging for UI crashes.

## Scope

### 1) Stop using DateTimeOffset in ORDER BY (SQLite-safe ordering)
- Update ALL EF queries that order by DateTimeOffset (CreatedAtUtc/StartedAtUtc/CompletedAtUtc) to use a SQLite-supported ordering key.
- Preferred minimal approach (no migrations):
  - Replace `OrderByDescending(x => x.CreatedAtUtc)` with ordering by stored column text:
    - Use `EF.Property<string>(entity, nameof(Entity.CreatedAtUtc))`
  - For “latest” logic use a COALESCE sort key:
    - CompletedAtUtc ?? StartedAtUtc ?? CreatedAtUtc
- Ensure app can open cases and refresh “latest jobs” without throwing.

### 2) Centralize job query logic into an Infrastructure service (testable)
- Create `IJobQueryService` in Infrastructure (or similar) with methods:
  - `GetLatestJobForEvidenceAsync(caseId, evidenceItemId, jobType, ct)`
  - `GetRecentJobsAsync(caseId, take, ct)`
- Move the SQLite-safe ordering logic here.
- Update ViewModels to call the service instead of embedding LINQ directly.

### 3) Add SQLite integration tests that would have caught this crash
In `tests/CaseGraph.Infrastructure.Tests`:
- Create a temp SQLite workspace DB (file-based or SQLite in-memory with shared cache),
  apply migrations / EnsureCreated as appropriate.
- Seed minimal Case + Evidence + JobRecord rows with varying timestamps.
- Test 1: `GetLatestJobForEvidenceAsync` does NOT throw and returns correct “latest”.
- Test 2: `GetRecentJobsAsync` does NOT throw and returns jobs in expected order.

These tests must execute the real SQLite provider (NOT EF InMemory).

### 4) Improve UI exception logging (no silent crashes)
- Ensure any exception shown in the UI error dialog is logged to file with:
  - Exception.ToString()
  - CorrelationId (new Guid per exception)
  - Active CaseId (if available)
- Ensure WPF global handlers are wired:
  - `Application.DispatcherUnhandledException`
  - `TaskScheduler.UnobservedTaskException`
  - `AppDomain.CurrentDomain.UnhandledException`
All should log and then show a user-friendly dialog (or reuse existing dialog).

## Acceptance Criteria (Testable)
- [ ] Opening a case no longer crashes due to DateTimeOffset ORDER BY translation.
- [ ] SQLite integration tests exist and pass; they would fail if DateTimeOffset ORDER BY reappears.
- [ ] UI exceptions are written to the rolling log with correlation ID.
- [ ] `dotnet test` passes.
- [ ] `dotnet run --project src/CaseGraph.App` opens and can open a case.

## Manual Verification
1) Launch app, create/open case → no crash.
2) Import evidence, run Parse Messages → no crash when refreshing “latest parse job”.
3) Trigger a controlled exception (optional) and confirm it appears in the log file with a correlation ID.

## Codex Instructions
- Implement ONLY this ticket scope.
- Keep queries AsNoTracking and use short-lived DbContexts (IDbContextFactory recommended).
- Update Docs/TICKETS.md Upcoming + Completed at end of pass.
- End with summary, files changed, steps to verify, tests run.



## Upcoming Tickets
- T0010 - Association graph v1 and relationship visualization (pending spec authoring).

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
