# Ticket Index

This file tracks planned, active, and completed tickets.

## How to use
- The single source of truth for ticket execution is this file.
- Codex executes only the ticket listed in **Active Ticket** with scope defined in **Active Ticket Spec**.
- Every ticket pass must update:
  - **Upcoming Tickets** (current and deduplicated)
  - **Completed Tickets** (append-only)

## Active Ticket
- ID: T0008C
- Title: Evidence Drawer “Latest Messages Parse Job” Live Refresh + Terminal Sync (no stale mid-parse status)

## Active Ticket Spec
```md
# Ticket: T0008C - Evidence Drawer “Latest Messages Parse Job” Live Refresh + Terminal Sync

## Goal
Fix the Evidence Drawer panel so “Latest Messages Parse Job” always reflects the true latest job state:
- shows 100% and terminal summary on completion
- shows live progress while running
- never stays stuck displaying a mid-parse snapshot after the job is already Succeeded

## Context / Evidence
Status bar correctly shows terminal completion for MessagesIngest (Succeeded/100%/Extracted N),
but Evidence Drawer still shows “Succeeded: Parsing… (X/Y)” where X is stale (e.g., 4345/10001).

This indicates Evidence Drawer is not refreshing its job viewmodel on job updates and/or is reading tracked EF entities.

## Scope

### In scope
1) **Evidence Drawer must bind to live job state**
   - Add/adjust an `EvidenceDrawerViewModel` property for latest messages parse job, e.g.:
     - `LatestMessagesParseJob` (JobSummaryVm)
     - fields: Status, Progress, StatusMessage, StartedAtUtc, CompletedAtUtc, JobId

2) **Refresh strategy (choose one, prefer event-driven)**
   - Preferred: subscribe Evidence Drawer VM to the existing job update event stream used by the status bar/review queue.
     - When a job update arrives for:
       - JobType == MessagesIngest AND EvidenceItemId == currently selected evidence
       - update the drawer’s `LatestMessagesParseJob` properties and raise PropertyChanged.
   - Fallback: poll while running
     - while selected evidence has a running MessagesIngest job, poll DB every 250–500ms to refresh the latest job summary
     - stop polling once terminal state reached

3) **DB reads must be fresh and not tracked**
   - Any DB query used by the drawer to fetch job summaries MUST:
     - use `IDbContextFactory<WorkspaceDbContext>` (or create a new scoped context per query)
     - use `.AsNoTracking()`
   - Query: “latest MessagesIngest job for selected EvidenceItemId”
     - ORDER BY CreatedAtUtc DESC LIMIT 1

4) **Terminal sync behavior**
   - On terminal status (Succeeded/Failed/Canceled/Abandoned):
     - drawer must show terminal StatusMessage (e.g., “Succeeded: Extracted 10001 message(s).”)
     - drawer progress must display 100%
     - Cancel button in drawer must be disabled/hidden for terminal jobs

5) **Manual refresh button**
   - Add a small “Refresh” icon/button inside Evidence Drawer job panel to force re-query of latest job summary.
   - Useful for debugging and user confidence.

## Acceptance Criteria (Testable)
- [ ] Run MessagesIngest on 10k XLSX:
  - drawer shows progress moving during run
  - within ≤ 1s of completion, drawer shows Succeeded + 100%
  - drawer shows terminal message (not stale “Parsing… X/Y”)
- [ ] Cancel button is disabled/hidden when job is terminal
- [ ] DB queries used by drawer use AsNoTracking and a fresh context
- [ ] dotnet build passes
- [ ] dotnet test passes (add at least 1 UI VM unit test if feasible, otherwise skip tests but keep build green)

## Deliverables
- UI: Evidence Drawer job panel live-refresh fix + refresh button
- Code: ViewModel updates + job update subscription or polling loop (with CancellationToken)
- Tests: (optional) VM-level unit test for refresh logic

## Implementation Notes / Edge Cases
- Ensure we do not leak timers/event handlers:
  - unsubscribe on drawer close / evidence selection change
  - cancel polling CTS on selection change
- If the selected evidence changes mid-job, the drawer should stop listening to the old job and switch to the new one.

## Codex Instructions
- Implement ONLY this ticket scope.
- Update Docs/TICKETS.md Completed section when done.
- Append ADR only if a new architectural decision is introduced (likely not needed).



## Upcoming Tickets (Planned)
- ?

---

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
