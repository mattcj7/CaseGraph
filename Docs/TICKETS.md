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
- Title: Fix SQLite DateTimeOffset ORDER BY crashes + Log UI exceptions + SQLite integration tests for “Open Case” queries

## Active Ticket Spec

# Ticket: T0009B - Fix SQLite DateTimeOffset ORDER BY crashes + Log UI exceptions + SQLite integration tests for “Open Case” queries

## Goal
1) Eliminate app crashes caused by EF Core SQLite failing to translate `OrderBy(DateTimeOffset)` (common on Open Case / Recent Activity / Latest Job).
2) Ensure any UI exception shown to the user is ALSO written to the rolling log with a correlation ID.
3) Add SQLite-backed integration tests that execute the same query paths used when opening a case so these regressions are caught by `dotnet test`.

## Context / Evidence
- Crash dialog previously showed: "SQLite does not support expressions of type 'DateTimeOffset' in ORDER BY clauses"
- workspace.db stores timestamps as TEXT (CreatedAtUtc/LastOpenedAtUtc/AddedAtUtc/etc.)
- app log does not currently capture UI exceptions reliably.

## Scope

### A) Fix ORDER BY on DateTimeOffset (SQLite-safe ordering)
Update ALL EF queries that order by timestamp properties (DateTimeOffset in model) to avoid DateTimeOffset expressions in ORDER BY.

Allowed patterns (choose one consistently):
1) **Order by stored TEXT column using EF.Property<string>**
   Example:
   - Replace: `.OrderByDescending(x => x.LastOpenedAtUtc)`
   - With: `.OrderByDescending(x => EF.Property<string>(x, nameof(CaseRecord.LastOpenedAtUtc)))`

2) For “latest” logic, use COALESCE sort key:
   - CompletedAtUtc ?? StartedAtUtc ?? CreatedAtUtc
   - Implement via projection:
     - `SortKey = EF.Property<string>(x, "CompletedAtUtc") ?? EF.Property<string>(x,"StartedAtUtc") ?? EF.Property<string>(x,"CreatedAtUtc")`
     - Order by SortKey (string)

Apply this anywhere it exists, at minimum:
- Case list ordering (LastOpenedAtUtc / CreatedAtUtc)
- Evidence list ordering (AddedAtUtc)
- Recent activity ordering (AuditEventRecord.TimestampUtc)
- Recent jobs / latest job ordering (JobRecord timestamps)

### B) Centralize queries in testable services
Create Infrastructure services to keep ViewModels thin and make testing easy:
- `ICaseQueryService`:
  - `GetRecentCasesAsync(...)`
- `IAuditQueryService`:
  - `GetRecentAuditAsync(caseId, take, ct)`
- `IJobQueryService`:
  - `GetLatestJobForEvidenceAsync(caseId, evidenceId, jobType, ct)`
  - `GetRecentJobsAsync(caseId, take, ct)`

Move the SQLite-safe ordering logic here. Use AsNoTracking + short-lived DbContexts (IDbContextFactory).

### C) Log UI exceptions reliably with correlation IDs
Wire global exception handlers in App startup:
- Application.DispatcherUnhandledException
- TaskScheduler.UnobservedTaskException
- AppDomain.CurrentDomain.UnhandledException

Requirements:
- Generate CorrelationId (Guid) per exception and include it in:
  - log entry
  - UI dialog text (“CorrelationId: ...”)
- Log Exception.ToString() + current CaseId (if known) + active view/action if available.

### D) Add SQLite-backed integration tests for open-case query paths
In `tests/CaseGraph.Infrastructure.Tests` (or a dedicated test project):
- Use real SQLite provider (Microsoft.Data.Sqlite + UseSqlite)
- Create a temp DB file (preferred for reliability) or in-memory with kept-open connection
- Apply migrations or EnsureCreated
- Seed Case/Evidence/Job/Audit rows with timestamp values

Add tests that:
- Execute `GetRecentCasesAsync`, `GetRecentAuditAsync`, `GetLatestJobForEvidenceAsync`, `GetRecentJobsAsync`
- Assert:
  - no translation exception is thrown
  - ordering is correct
These tests must fail if DateTimeOffset ORDER BY is reintroduced.

## Acceptance Criteria (Testable)
- [ ] Opening an existing/old case no longer crashes.
- [ ] UI exception dialogs always also write to log with CorrelationId.
- [ ] SQLite integration tests exist and pass; they would fail if DateTimeOffset ORDER BY returns.
- [ ] `dotnet test` passes.

## How to Verify Manually
1) `dotnet run --project src/CaseGraph.App`
2) Open an old case (the one that previously crashed) → must not crash.
3) Confirm `logs/app-*.log` includes any thrown UI exceptions with CorrelationId.
4) `dotnet test`

## Codex Instructions
- Implement ONLY this ticket scope.
- No silent merges; follow offline-first constraints.
- Keep queries AsNoTracking and contexts short-lived.
- End with summary + files changed + verify steps + tests run.


## Upcoming Tickets
- T0009B - Stabilize Open Case crashes (SQLite DateTimeOffset ORDER BY) + UI exception logging + SQLite integration tests (pending spec authoring)
- T0010 - Association graph v1 and relationship visualization (pending spec authoring)
- T0011 - Workflow hardening (AGENTS.md + DEBUGGING.md + validate script + Codex prompt template) (pending spec authoring)
- T0012 - Smoke tests (WPF XAML load + SQLite translation tests) (pending spec authoring)

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
