# Ticket Index

This file tracks planned, active, and completed tickets.

## How to use
- This file is the **ticket index** (planned/active/completed). It remains append-only for Completed Tickets.
- The source of truth for executing a ticket is:
  - `Docs/Tickets/TXXXX.md` (spec)
  - `Docs/Tickets/TXXXX.context.md` (context pack; whitelist + links)
  - `Docs/Tickets/TXXXX.closeout.md` (what shipped + tests run)
- Standard workflow:
  1) `dotnet run --project Tools/TicketKit -- init TXXXX "Title"`
  2) Create/Update `Docs/Tickets/TXXXX.md`
  3) Fill `Docs/Tickets/TXXXX.context.md` (whitelist + constraints)
  4) Gate before coding: `dotnet run --project Tools/TicketKit -- verify TXXXX --strict`
  5) After merge, fill `Docs/Tickets/TXXXX.closeout.md`
- Codex should be given **only**:
  - `Docs/Tickets/TXXXX.md`
  - `Docs/Tickets/TXXXX.context.md`
  - plus linked headings in Docs/INVARIANTS.md (do not paste global rules)

## Active Ticket


## Upcoming Tickets (deduplicated)





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
- 2026-02-28 - T0024 - Added a paged/cancellable Messages-first Timeline view and SQLite query service with target/global-person/date/direction/text filters, provenance actions, audit logging for timeline searches, and deterministic infrastructure coverage for ordering/filter/audit behavior.
- 2026-03-03 - T0025 - Added Reports v1 with dossier export workflow for targets/global persons, evidence-cited sections (EvidenceItem + SourceLocator), non-blocking cancellable export with progress, audited ReportExported events, and deterministic tests.
- 2026-03-05 - T0026 - Added LocationObservation schema/migration/indexes, queue-backed CSV/JSON/PLIST LocationsIngest with provenance and tolerant parsing, Locations page (filters + paging/virtualization + View Source/Copy Citation), and ingest/search audit coverage with deterministic tests.
- 2026-03-07 - T0027 - Added the Incident Window workspace (wizard + comms/geo/co-location results), cancellable audited incident-window queries with provenance actions, and deterministic geo math/filter/audit coverage.
- 2026-03-07 - T0028 - Fixed `ReportsViewModel` startup construction null-safety by correcting initialization order, hardening early-state command notifications, and adding deterministic constructor/DI regression tests.
- 2026-03-07 - T0028 - Expanded the hotfix into a Reports/Timeline navigation stability patch by hardening constructor-time initialization for both page viewmodels, adding explicit tab activation failure logging, and covering Reports/Timeline constructor, DI, and route/view creation regressions.
- 2026-03-08 - T0028 - Stabilized post-migration startup by making the message-FTS reconciliation observable and skip-safe on steady-state workspaces, added explicit host/main-window lifecycle diagnostics, and covered startup progression plus Reports/Timeline activation with deterministic regressions.
- 2026-03-08 - T0028 - Completed the startup and page-activation stabilization sweep by logging startup/activation boundaries, hardening post-migration FTS maintenance and Reports init-order, confirming Incident Window wiring stayed intact, and adding deterministic startup plus Reports/Timeline safety regressions.
- 2026-03-08 - T0029 - Added a visible startup progress window with real startup-stage reporting and elapsed time, preserved structured startup diagnostics, deferred safe non-critical finalization until after the main window shows, and added deterministic startup progress tests.
- 2026-03-08 - T0030 - Split readiness into startup, case-open, and feature-open phases, moved heavy message-search reconciliation out of global startup, added visible case/feature readiness orchestration, and covered the split with deterministic regressions.
- 2026-03-09 - T0031 - Split Search readiness into a fast FTS status check plus deferred maintenance, kept Search responsive with a visible preparing state, separated readiness/maintenance logs from query execution, and added deterministic readiness regressions.
- 2026-03-09 - T0032 - Added reusable performance instrumentation and budget thresholds, timed startup/case-open/search/timeline/reports/incident plus key maintenance flows, and added deterministic slow-path/log-label regression coverage.
- 2026-03-10 - T0033 - Added main-shell minimize/maximize/restore/close controls, title-bar drag and double-click maximize behavior, maximized drag-restore handling, and focused shell regression coverage while preserving startup and navigation stability.
- 2026-03-10 - T0033 - Finalized shell polish by reserving a fixed top-right window-control strip, making title/header content the truncating region at narrow widths, and preserving stable drag/maximize/restore behavior with added layout-width regression coverage.
- 2026-03-10 - T0033 - Fixed the shell title-bar startup regression by replacing the invalid `ColumnDefinition.Width` static numeric binding with a valid fixed-width control strip, preserving always-visible top-right window controls and narrow-width truncation behavior.
- 2026-03-10 - T0034 - Added a shared background maintenance manager with lifecycle logging and duplicate-request dedupe, surfaced readiness/maintenance banners in Search/Timeline/Reports/Incident Window, kept deferred maintenance non-blocking, and added deterministic maintenance/banner regressions.
- 2026-03-10 - T0035 - Hardened Association Graph render/layout validation, skipped malformed graph items, added per-component MSAGL fallback/degraded rendering with structured diagnostics, and covered non-throw graph edge cases with deterministic regressions.
- 2026-03-10 - T0035 - Finalized Association Graph snapshot export by capturing the rendered graph canvas at validated non-zero bounds, adding structured PNG export diagnostics, and preserving the earlier layout-hardening/fallback behavior.
