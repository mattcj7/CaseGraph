# Ticket Index

This file tracks planned, active, and completed tickets.

## How to use
- The single source of truth for ticket execution is this file.
- Codex executes only the ticket listed in **Active Ticket** with scope defined in **Active Ticket Spec**.
- Every ticket pass must update:
  - **Upcoming Tickets** (current and deduplicated)
  - **Completed Tickets** (append-only)

## Active Ticket

# Ticket: T0009C - Workspace open/create hardening (EF migrations + crash logging + smoke tests)

## Goal
Stop crashes when:
- Opening an old case workspace DB
- Creating a new case
- Starting a job (e.g., MessagesIngest) after opening/creating a case

Also ensure every crash produces a usable log + stack trace, and add smoke tests that would catch these regressions.

## Context / Diagnosis
We have old workspace DBs that only contain the initial migration and are missing later tables (Targets/Identifiers).
Current app code appears to open/switch workspaces without applying migrations, and some UI-thread exceptions are not being captured by the logger (app exits with no helpful stack trace in the app log).

## Requirements

### A) Always migrate on workspace open/switch
- Replace any `EnsureCreated()` usage for existing DBs with `Database.Migrate()`.
- Ensure migration runs:
  1) at app startup (default workspace root DB)
  2) whenever user opens/switches to an existing workspace DB path
- If migration fails:
  - Do NOT crash
  - Show a friendly error dialog with:
    - summary (what failed)
    - the DB path
    - the log path
    - a “Copy diagnostics” button
  - Log full exception details

### B) Crash-proof exception capture (WPF + background tasks)
Add global handlers in `CaseGraph.App` startup:
- `Application.DispatcherUnhandledException`
- `AppDomain.CurrentDomain.UnhandledException`
- `TaskScheduler.UnobservedTaskException`

Behavior:
- Log full exception + stack trace as FATAL
- Flush logs (best-effort)
- Show a crash dialog with:
  - “What happened”
  - “Where the logs are”
  - “Copy diagnostics”
- Then shutdown cleanly (or allow continue only if safe—default to shutdown)

### C) Add a Diagnostics surface in the UI
Add a simple “Diagnostics” view (or modal) reachable from the main window:
- Shows:
  - App version / git commit (if available)
  - Workspace root + active workspace DB path
  - Last 50 log lines (read-only)
  - Button: “Open logs folder”
  - Button: “Copy diagnostics”

### D) Smoke tests to catch these crashes
Add integration-ish tests in `CaseGraph.Infrastructure.Tests` (or appropriate test project):

1) `Workspace_Migrate_OldDb_UpgradesToLatest`
- Arrange: copy an “old schema” DB fixture into a temp folder
- Act: run initializer / open workspace
- Assert:
  - `__EFMigrationsHistory` contains latest migration id
  - expected tables exist (`TargetRecord`, `IdentifierRecord`, etc.)
  - no exceptions thrown

2) `Workspace_Open_OldDb_DoesNotThrow_WhenLoadingCase`
- Arrange: old DB fixture contains at least one CaseRecord
- Act: open workspace and load basic case summary query
- Assert: no throw

3) `App_SelfTest_ReturnsSuccess`
Add a CLI flag to the app project:
- `CaseGraph.App --self-test`
which:
  - builds host
  - opens workspace
  - migrates
  - runs a trivial query
  - exits 0 on success, non-zero on failure
Then test it from a test using `ProcessStartInfo`.

### E) Guardrails: jobs should never crash the app
- Confirm JobRunner exceptions are contained and only affect job status.
- Any job failure must:
  - mark job failed
  - store error text
  - log exception
  - NOT bring down the UI process

### F) Documentation / Workflow
- Update `Docs/TICKETS.md`:
  - Keep **Upcoming Tickets** and **Completed Tickets** accurate.
  - When T0009C is done, move it to Completed with date + 1-line summary.
- Add an ADR entry in `Docs/ADR.md`:
  - “ADR: Workspace DB migration strategy”
  - State that we use EF Migrations and run `Database.Migrate()` on open.

## Acceptance Criteria
- Opening the provided old-case DB no longer crashes; it migrates and loads.
- Creating a new case no longer crashes.
- Any crash produces a clear log entry with stack trace and shows a crash dialog with log path.
- Tests added and passing:
  - old DB migrates to latest
  - open old case doesn’t throw
  - self-test passes
- Docs updated (Tickets + ADR).



## Codex Instructions
- Implement ONLY this ticket scope.
- No silent merges; follow offline-first constraints.
- Keep queries AsNoTracking and contexts short-lived.
- End with summary + files changed + verify steps + tests run.


## Upcoming Tickets
- T0010 - Association graph v1 and relationship visualization (pending spec authoring).
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
- 2026-02-18 - T0009C - Hardened workspace open/create with migration-on-open, fatal crash diagnostics UX, diagnostics page, self-test CLI, and old-DB smoke coverage.
