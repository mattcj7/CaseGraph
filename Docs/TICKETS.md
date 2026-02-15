# Ticket Index

This file tracks planned, active, and completed tickets.

## How to use
- The single source of truth for ticket execution is this file.
- Codex executes only the ticket listed in **Active Ticket** with scope defined in **Active Ticket Spec**.
- Every ticket pass must update:
  - **Upcoming Tickets** (current and deduplicated)
  - **Completed Tickets** (append-only)

# T0009A - Stabilization Hotfix: App Launch + Job Progress Finalization + Evidence Verify

## Goal
Fix current instability introduced around T0009/T0008 series:
- App sometimes runs without opening a window (WPF startup failure)
- Jobs show Succeeded but progress is < 1.0 and status messages are wrong
- EvidenceVerify jobs repeatedly fail SHA mismatch (likely path/hash mismatch + duplicate enqueue)
- Debug/TestLongRunningDelay job can fail due to invalid payload

## Observed Symptoms (from app log)
- Startup failure: XamlParseException cannot find StaticResource "SpaceTop8" (MainWindow.xaml ~line 222)
- MessagesIngest completes quickly but JobRecord.Progress ends < 1.0; status message sometimes stuck on "Persisting parsed messages..."
- EvidenceVerify enqueued twice for the same evidence; first completes, second fails with SHA-256 mismatch
- TestLongRunningDelay jobs fail with "Invalid TestLongRunningDelay payload."

## Requirements
### R1 — Fix WPF startup crash
- Locate where `SpaceTop8` is referenced (MainWindow.xaml ~line 222).
- Ensure `SpaceTop8` exists in merged dictionaries at runtime with correct type (Thickness/Double/etc based on usage).
- Add missing spacing keys if needed and ensure dictionaries are merged in App.xaml or Theme root.

### R2 — Make Job progress/status terminal-safe (no overwrites after completion)
- Ensure progress updates cannot overwrite a terminal job (Succeeded/Failed/Canceled).
- Ensure progress is monotonic (never decreases).
- When a job completes successfully, persist:
  - Status = Succeeded
  - Progress = 1.0
  - StatusMessage = final summary (e.g., "Extracted 10000 message(s).")
- If progress events arrive late/out-of-order, ignore them once Status is terminal and/or CompletedAtUtc is set.

### R3 — Fix MessagesIngest “no sheets found” guidance + failing test
- Ensure the final JobRecord.StatusMessage for “no recognized message sheets” remains the guidance message.
- Update/repair failing test `MessagesIngestJob_XlsxWithoutRecognizedSheets_ReportsGuidance` so it validates:
  - Status == Succeeded
  - StatusMessage contains the guidance string (and is not overwritten by progress text)

### R4 — EvidenceVerify SHA mismatch + duplicate verify jobs
- Ensure the hash stored + verified is consistently for the immutable Evidence Vault copy (not the original source path).
- Prevent double-enqueue of EvidenceVerify for the same evidence while another verify is queued/running.
- If a real mismatch happens, surface a clear operator message (chain-of-custody warning), but do not crash the app.

### R5 — TestLongRunningDelay safety
- Make TestLongRunningDelay:
  - Not enqueue in production builds (preferred), OR
  - Payload parsing is safe: missing/invalid payload results in a default delay or a clean job failure message, not an exception that destabilizes runtime.

### R6 — Add “last-ditch” exception visibility
- Ensure all unhandled UI exceptions and job exceptions are logged.
- If MainWindow fails to construct/show, show a blocking error dialog and exit cleanly (no silent background process).

## Acceptance Criteria
- App launches reliably (MainWindow appears) on existing workspace DBs.
- MessagesIngest jobs end with Progress == 1.0 and correct final StatusMessage.
- Cancel/queued/running jobs do not overwrite terminal job records after completion.
- EvidenceVerify does not repeatedly fail SHA mismatch due to path confusion / double-enqueue.
- `dotnet test` passes (including the previously failing guidance test).

## Dev Notes / Implementation Hints
- Consider enforcing terminal-safe updates in the JobRecord persistence layer:
  - UPDATE ... WHERE Status IN (Queued, Running)
- Consider clamping and monotonic progress:
  - job.Progress = max(job.Progress, incomingProgress)
- If needed: add timestamps/sequence numbers to ignore stale progress events.


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
