# Ticket Index

This file tracks planned, active, and completed tickets.

## How to use
- The single source of truth for ticket execution is this file.
- Codex executes only the ticket listed in **Active Ticket** with scope defined in **Active Ticket Spec**.
- Every ticket pass must update:
  - **Upcoming Tickets** (current and deduplicated)
  - **Completed Tickets** (append-only)

## Active Ticket
- ID: T0008B
- Title: Terminal Job Finalize (force 100%) + Final StatusMessage + Tests (fix Succeeded but <100% progress)

## Active Ticket Spec
```md
# Ticket: T0008B - Terminal Job Finalize (force 100%) + Final StatusMessage + Tests

## Goal
Fix the bug where a job finishes with `Status=Succeeded` but `Progress < 1.0` and `StatusMessage` still reflects a mid-parse state (e.g., “Parsing… (150/500)”).
Ensure that ALL jobs end with correct terminal fields so the UI can reliably show completion.

## Evidence / Diagnosis
SQLite `JobRecord` rows show:
- `Status = Succeeded`
- `CompletedAtUtc` set
- `Progress < 1.0`
- `StatusMessage` stuck at last progress update
Therefore the runner/handler is not performing a final terminal overwrite save (or it is being throttled/skipped).

## Scope

### 1) Single ticket source enforcement
- Tickets are defined/executed from `Docs/TICKETS.md` only.
- Ensure there is NO root `TICKET.md` and NO root `TICKETS.md`. If present, delete (use `git rm` if tracked).

### 2) JobRunner: enforce terminal overwrite on completion (non-throttled)
In the job execution pipeline (runner), implement a guaranteed terminal finalize write that ALWAYS runs:

For terminal states Succeeded/Failed/Canceled/Abandoned:
- set `CompletedAtUtc = now`
- overwrite `Status = <terminal>`
- overwrite `Progress = 1.0`
- overwrite `StatusMessage` with a terminal message:
  - Succeeded: `Succeeded: <summary>`
  - Failed: `Failed: <error summary>`
  - Canceled: `Canceled`
  - Abandoned: `Abandoned (app shutdown before completion)`
- Failed must set `ErrorMessage` (full exception string).

Requirements:
- Terminal finalize MUST occur in `finally` and must not be throttled.
- Terminal finalize MUST persist even if cancellation token is requested:
  - Use `CancellationToken.None` (or a safe non-cancel token) for the final `SaveChangesAsync` so a cancel request doesn’t prevent saving terminal state.
- Publish a final JobUpdate event after terminal finalize.

### 3) MessagesIngest: produce a final summary for StatusMessage
Ensure MessagesIngest handler returns enough info for a meaningful terminal message:
- `messagesExtracted` (int)
- (optional) `threadsCreated` (int)

Runner uses this to set:
- `StatusMessage = $"Succeeded: Extracted {messagesExtracted} message(s)."`

### 4) Ensure queued cancel and running cancel still produce terminal finalize
If Cancel is requested:
- queued cancel:
  - set Status=Canceled, CompletedAtUtc, Progress=1.0, StatusMessage="Canceled"
- running cancel:
  - handler should throw `OperationCanceledException`
  - runner finalizes terminal state exactly as above

### 5) Tests (deterministic)
Extend `tests/CaseGraph.Infrastructure.Tests`:

Minimum tests:
1) Terminal finalize on success:
   - Run a MessagesIngest job (or deterministic fake job)
   - Assert the saved JobRecord ends with:
     - Status=Succeeded
     - Progress=1.0
     - StatusMessage starts with `Succeeded:`
     - CompletedAtUtc not null

2) Terminal finalize on cancel:
   - Start a job that runs long enough to cancel deterministically (use a test-only fake job type or inject a small delay in a test handler).
   - Cancel it.
   - Assert:
     - Status=Canceled
     - Progress=1.0
     - StatusMessage="Canceled"
     - CompletedAtUtc set

3) Terminal finalize on failure:
   - Run a test fake job that throws.
   - Assert:
     - Status=Failed
     - Progress=1.0
     - StatusMessage starts with `Failed:`
     - ErrorMessage not null

Notes:
- Prefer a test-only fake job handler registered only in tests to keep determinism and avoid relying on XLSX timing.

### 6) Docs updates (same pass)
- Update `Docs/TICKETS.md` Upcoming + Completed at end of pass:
  - Move T0008B to Completed with date + one-line summary once AC met
- Append ADR entry in `Docs/ADR.md` documenting:
  - terminal finalize strategy (always force progress=1.0 in finally, save with non-cancel token)
  - commit hash left as `<fill in after commit>`

## Acceptance Criteria (Testable)
- [ ] Any completed job row in `JobRecord` has:
  - [ ] `CompletedAtUtc` set
  - [ ] `Progress = 1.0`
  - [ ] terminal `StatusMessage` (not mid-parse)
- [ ] MessagesIngest ends with `StatusMessage = "Succeeded: Extracted N message(s)."`
- [ ] dotnet build passes
- [ ] dotnet test passes (includes success/cancel/failure finalize tests)
- [ ] ADR entry appended and Docs/TICKETS.md updated (Upcoming + Completed)

## Manual Verification
1) Run app, parse 500-row XLSX
2) Query DB:
   ```sql
   SELECT Status, Progress, StatusMessage, CompletedAtUtc
   FROM JobRecord
   ORDER BY CreatedAtUtc DESC
   LIMIT 1;
3) Confirm: Succeeded, Progress=1.0, StatusMessage “Succeeded: Extracted 500 message(s).”

Codex Instructions

Ticket source of truth is this Active Ticket Spec in Docs/TICKETS.md.

Implement ONLY this ticket scope.

Update Docs/TICKETS.md Upcoming + Completed at end of pass.

Append ADR entry as required.

End output with summary, files changed, build/run steps, verify steps, tests.


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
