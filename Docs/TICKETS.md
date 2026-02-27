# Ticket Index

This file tracks planned, active, and completed tickets.

## How to use
- The single source of truth for ticket execution is this file.
- Codex executes only the ticket listed in **Active Ticket** with scope defined in **Active Ticket Spec**.
- Every ticket pass must update:
  - **Upcoming Tickets** (current and deduplicated)
  - **Completed Tickets** (append-only)

# Ticket: T0023 - Ticket Context Packs + Invariants + Dev Tooling (“TicketKit”)

## Goal
Bake the context-minimization workflow into the repo so each future ticket is started with a small, deterministic bundle: Ticket + Context Pack + Invariants links—optionally verified by a local tool.

## Context
As CaseGraph Offline grows, copying global rules and past history into every new ticket bloats LLM/Codex context and increases drift. We want a repo-native, repeatable approach that centralizes invariants once, creates a per-ticket Context Pack, and (optionally) enforces a small “context budget” offline.

## Scope
### In scope
- Add canonical docs to repo:
  - Docs/INVARIANTS.md
  - Docs/ARCHITECTURE.md
  - Docs/DATA_MODEL.md
  - Docs/DECISIONS/ADR-TEMPLATE.md
  - Docs/Tickets/_TEMPLATES/TXXXX.context.md
  - Docs/Tickets/_TEMPLATES/TXXXX.closeout.md
  - Docs/Ticket_Context_Workflow.md
- Create an offline local dev tool (C#/.NET console app) Tools/TicketKit that can:
  - `ticketkit init TXXXX "Title"` → creates:
    - Docs/Tickets/TXXXX.context.md (from template)
    - Docs/Tickets/TXXXX.closeout.md (from template)
    - (optional) an ADR stub from template via flag (e.g., `--adr "Title"`)
  - `ticketkit verify TXXXX` → checks:
    - context/closeout files exist
    - required headings exist (basic validation)
    - referenced file links exist (basic validation)
    - optional “context budget” warnings (best-effort):
      - touched top-level folders count
      - number of migrations touched
- Add lightweight docs: “How to start a ticket with Codex using the prompt bundle.”

### Out of scope
- Any runtime CaseGraph product features or UI additions
- CI/GitHub Actions enforcement (keep it local/offline for now)
- Automatic AI-generated context packs or code summarization

## Acceptance Criteria (Testable)
- [ ] Repo contains the canonical docs and templates listed in scope.
- [ ] `dotnet run --project Tools/TicketKit -- init T0023 "Ticket Context Packs"` creates:
  - [ ] Docs/Tickets/T0023.context.md
  - [ ] Docs/Tickets/T0023.closeout.md
- [ ] `init` does not overwrite existing files unless `--force` is provided.
- [ ] `dotnet run --project Tools/TicketKit -- verify T0023`:
  - [ ] returns exit code 0 when required files exist and validations pass
  - [ ] returns non-zero when required files are missing or required headings are missing
  - [ ] prints clear, actionable messages (what to fix and where)
- [ ] Tool operates offline (no network access required or attempted).
- [ ] `verify` handles missing `git` gracefully (budget checks are skipped with a clear message).
- [ ] Unit tests exist for template rendering, safe writes, and verify exit codes.

## Deliverables
- Code:
  - Tools/TicketKit/ .NET console app with `init` and `verify` commands
- UI:
  - None
- Database (migrations):
  - None
- Tests:
  - Unit tests for TicketKit (template rendering, safe write, verify rules)
- Docs:
  - Canonical docs/templates added
  - Workflow doc updated with the “Codex prompt bundle” instructions

## Data Model Changes
- None

## Provenance & Audit Requirements
### Provenance
N/A (dev tooling + docs only)

### Audit
N/A (dev tooling + docs only)

## Performance & Resource Management Requirements
- Use async/await for I/O when beneficial; avoid unnecessary blocking.
- Use streaming for file reads/writes; avoid loading large files into memory.
- Dispose all IDisposable resources deterministically.
- Support CancellationToken for longer operations (verify scans).
- Tool must remain fast on large repos (avoid heavyweight recursive parsing unless requested).

## Implementation Notes / Edge Cases
- Templates live in repo under Docs/Tickets/_TEMPLATES/ and are copied/expanded.
- Safe writes:
  - Default: do not overwrite existing files
  - `--force` overwrites existing files
- Basic validations for `verify`:
  - TXXXX.context.md contains required headings:
    - “## Purpose”
    - “## Canonical links (do not paste content)” (or equivalent)
    - “## Files to open (whitelist)”
  - TXXXX.closeout.md contains required headings:
    - “## What shipped”
    - “## Files changed (high-level)”
- Link existence check:
  - parse obvious `Docs/...` relative paths and confirm files exist
  - do not require perfect markdown link parsing—keep it resilient
- Git/budget checks (best-effort):
  - If `git` is available, use `git diff --name-only HEAD` (or configurable base) to approximate touched files.
  - If not available, print: “Budget checks skipped (git not available).”
- Budget checks:
  - touched top-level folders > 3 → warn (or fail under `--strict`)
  - migrations touched > 1 → warn (or fail under `--strict`)
- CLI UX:
  - `ticketkit help` prints usage examples
  - errors are actionable and include exit codes

## Test Plan
### Unit tests
- Template rendering:
  - replaces TXXXX and <Title> tokens correctly
- Safe writes:
  - refuses overwrite by default
  - overwrites with `--force`
- Verify:
  - missing files → non-zero exit code
  - missing required headings → non-zero exit code
  - valid files → exit code 0
  - `--strict` converts budget warnings into failures (if budget checks run)

### Integration tests (if applicable)
- Create a temp folder repo layout, run `init`, assert files exist and contain required headings.
- Run `verify` before and after deleting a required file.

## How to Verify Manually
1. Build tool: `dotnet build Tools/TicketKit/TicketKit.csproj`
2. Run init:
   - `dotnet run --project Tools/TicketKit -- init T0023 "Ticket Context Packs"`
3. Confirm created files exist in Docs/Tickets/.
4. Run verify:
   - `dotnet run --project Tools/TicketKit -- verify T0023`
5. Delete Docs/Tickets/T0023.context.md and re-run verify; confirm non-zero exit and actionable output.
6. Re-run init without --force; confirm it refuses to overwrite.
7. Re-run init with --force; confirm overwrite occurs.

## Codex Instructions
- Follow offline-only constraints (no network calls/telemetry).
- Do not touch product runtime behavior; this ticket is docs + dev tooling only.
- Keep build green; add tests; no migrations expected.
- End with: summary + files changed + steps to verify.


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
- 2026-02-25 - T0020 - Added `TargetMessagePresence` indexing with rebuild/incremental refresh on ingest/link changes, moved Where Seen and target-filtered search to the index, and added deterministic SQLite coverage that a single-message link backfills all matching messages while preserving provenance.
- 2026-02-25 - T0021 - Added global person registry tables and target linkage, enabled create/link/unlink global-person UI flows with cross-case visibility, introduced global-person message search filtering, and added deterministic multi-case SQLite coverage for shared identities and explicit global-identifier conflict handling.
- 2026-02-25 - T0022 - Added an Association Graph page with filters/pan-zoom/details/actions, implemented Infrastructure graph query + global-person grouping + PNG export path builder, added graph snapshot export UX/logging, and added deterministic SQLite tests for graph correctness/grouping plus export-path unit coverage.
- 2026-02-27 - T0023 - Added ticket context-pack canonical docs/templates, implemented offline `TicketKit` (`init`/`verify` with safe writes, link/heading checks, budget checks, strict mode, git-missing handling), and added deterministic unit coverage for rendering/write safety/verify behavior.
