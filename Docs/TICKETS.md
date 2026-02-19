# Ticket Index

This file tracks planned, active, and completed tickets.

## How to use
- The single source of truth for ticket execution is this file.
- Codex executes only the ticket listed in **Active Ticket** with scope defined in **Active Ticket Spec**.
- Every ticket pass must update:
  - **Upcoming Tickets** (current and deduplicated)
  - **Completed Tickets** (append-only)

## Active Ticket
- ID: T0010
- Title: Observability v1 + Workflow Hardening (structured logs, correlation IDs, debug bundle, Codex guardrails)

## Active Ticket Spec
```md
# Ticket: T0010 - Observability v1 + Workflow Hardening

## Goal
Make crashes and regressions diagnosable and prevent process drift:
1) Structured logging with correlation IDs across UI actions, jobs, and workspace operations
2) Guaranteed global exception capture (WPF + background tasks) so “silent crashes” always yield a stack trace in logs
3) A one-click “Debug Bundle” export (logs + workspace DB + config + last log lines)
4) Repo guardrails so Codex always updates tickets/ADRs and follows constraints

## Scope

### A) Structured logging (JSON) + scopes
- Switch logging output to JSON lines (one event per line).
- Standard fields on every event:
  - timestampUtc, level, eventName/eventId, correlationId
  - caseId (if available), evidenceId, jobId, actionName (if applicable)
- Add BeginScope helpers:
  - `ActionScope` (OpenCase/CreateCase/ParseMessages/CreateTarget)
  - `JobScope` (JobId, JobType)
  - `WorkspaceScope` (workspace path)
- Keep evidence content out of logs by default (no message bodies, no PII). Log counts/hashes instead.

### B) Global exception capture (no more missing stack traces)
Wire in App startup:
- Application.DispatcherUnhandledException
- TaskScheduler.UnobservedTaskException
- AppDomain.CurrentDomain.UnhandledException

Requirements:
- Generate correlationId if missing
- Log full Exception.ToString() with correlationId
- Show crash dialog containing correlationId + log folder path
- Flush logs best-effort and shutdown cleanly (no “ghost process”)

### C) Safe async command wrapper (logs success/fail)
Implement a common `AsyncRelayCommand` (or similar) that:
- logs start + success + failure (with durationMs)
- catches exceptions (never lets them escape UI thread)
- attaches correlationId scope for the whole action
Convert key UI actions to use it:
- OpenCase
- CreateCase
- ParseMessages
- CreateTarget (and any other common action)

### D) Debug Bundle export
Add a UI action “Export Debug Bundle” that:
- zips:
  - logs folder
  - active workspace.db + wal + shm
  - app config/settings (if any)
  - a small `diagnostics.json` file containing:
    - app version/commit (if available)
    - OS + dotnet version
    - active workspace path
    - last 2000 log lines (or last 1MB)
- Saves to user-selected location (default Desktop) and shows the file path.

### E) Workflow hardening artifacts
Add:
1) `AGENTS.md` in repo root with Codex rules:
   - tickets only in Docs/TICKETS.md
   - update Upcoming/Completed each run
   - run dotnet test before finishing
   - ADR update rules
   - offline-only constraints and provenance/audit rules
2) `Docs/DEBUGGING.md`:
   - where logs live
   - how to export bundle
   - what to attach when reporting a bug
3) `tools/validate-repo.ps1`:
   - fails if root ticket files exist
   - fails if Docs/TICKETS.md missing
   - fails if AGENTS.md missing

### F) Tests
Add tests that enforce observability behaviors:
1) A unit test for AsyncRelayCommand:
   - on exception, it logs failure and does not throw on UI dispatcher
2) A unit test that correlationId is present in log scope for an action
3) A lightweight test that Debug Bundle builder includes required files list (mock file system ok)

(Do not add external heavy packages; keep tests fast.)

## Acceptance Criteria
- [ ] Any crash shows correlationId and writes stack trace to log.
- [ ] OpenCase/CreateCase/ParseMessages/CreateTarget log start/end/fail with correlationId + duration.
- [ ] Debug Bundle export works and includes logs + workspace db (+ wal/shm).
- [ ] Repo guardrails files exist (AGENTS.md, Docs/DEBUGGING.md, tools/validate-repo.ps1).
- [ ] dotnet test passes.
- [ ] Docs/TICKETS.md updated (move T0010 to Completed with date + summary once verified).

## Codex Instructions
- Implement ONLY this ticket scope.
- Keep logs redacted (no evidence content).
- End with summary + files changed + verify steps + tests run.




```

## Upcoming Tickets
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
- 2026-02-19 - T0009D - Added safe async command containment for key flows, workspace initializer migration/query verification logging, and SQLite regression tests for empty-db initialization.
- 2026-02-19 - T0010 - Added structured JSON logging scopes/correlation, safe async action containment, debug bundle export UX, observability tests, and repository guardrail docs/scripts.
