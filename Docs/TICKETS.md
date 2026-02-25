# Ticket Index

This file tracks planned, active, and completed tickets.

## How to use
- The single source of truth for ticket execution is this file.
- Codex executes only the ticket listed in **Active Ticket** with scope defined in **Active Ticket Spec**.
- Every ticket pass must update:
  - **Upcoming Tickets** (current and deduplicated)
  - **Completed Tickets** (append-only)

## Upcoming Tickets
- (none)

## Active Ticket
- (none)

## T0022 - Association Graph v1 (case graph + global overlay + export)

### Goal
Provide a usable relationship visualization that helps investigators quickly see:
- clusters/crews, connectors/bridges, and high-volume links
- how identifiers connect to targets
- optional “global person” overlay so cross-case identity is usable

### Context
We now have:
- Targets + identifiers
- Target ↔ message presence indexing (T0020)
- Global Person Registry cross-case (T0021)
- SQLite lock resilience (T0018)

This ticket turns that into an explorable graph UI.

---

## Scope

### A) New UI page: Association Graph
Add a new navigation entry and page (WPF):
- Page: `AssociationGraphView.xaml`
- ViewModel: `AssociationGraphViewModel`
- A left panel for filters, a main graph viewport, and a right “Details” panel.

Minimum UI controls:
- Case selector (current case only is fine; no multi-case browsing yet)
- Toggle: **Include Identifiers** (default ON)
- Toggle: **Group by Global Person** (default OFF)
- Slider/box: **Min Edge Weight** (default 2)
- Search box: highlights nodes by text match
- Button: **Rebuild Graph**
- Button: **Export Graph Snapshot (PNG)**

Interaction:
- Click node → show details (type, name/value, counts, “last seen” best-effort)
- From details:
  - **Open Target** (if node is Target)
  - **Search messages** filtered to that Target/Identifier
- Hover edge → show weight + interpretation (e.g., “shared threads: X”, “co-occurrence events: Y”)

### B) Graph definition (v1)
Node types:
- Target (case target)
- Identifier (phone/email/handle) — when Include Identifiers is ON
- Global Person (optional overlay) — when Group by Global Person is ON

Edge types:
1) Target ↔ Identifier
- source: Target’s identifiers table
- weight: 1 (or optionally “messages matched” count if cheap)

2) Target ↔ Target
- derived from co-occurrence in communications:
  - Use `TargetMessagePresence` index to compute: targets that appear in the same **thread** and/or same **message event**
- Edge weight definition (v1):
  - `weight = distinctThreadCount` (preferred) OR `distinctMessageEventCount`
- Optional: store additional metrics (threadCount, eventCount, lastSeenUtc)

3) (Optional) Identifier ↔ Identifier
- Only if cheap and helpful; otherwise defer to T0022A.

Global overlay behavior:
- If Group by Global Person is ON:
  - Collapse multiple case targets that map to the same Global Person into a single “Person” node
  - Edges merge: weights sum (or max), and details show the contributing targets/identifiers.

### C) Infrastructure: Graph query service
Add an Infrastructure query/service that builds the graph model from SQLite:

Proposed:
- `AssociationGraphQueryService.BuildAsync(caseId, options, ct)` → returns:
  - `GraphNode[]` (Id, Kind, Label, Optional metadata)
  - `GraphEdge[]` (SourceId, TargetId, Kind, Weight, Optional metadata)

Must:
- use set-based queries, avoid per-node N+1
- be deterministic for tests
- work well with 10k–100k message events (best-effort for v1)

### D) Rendering library (recommended)
Use a proven offline graph layout renderer for WPF.

Recommendation:
- MSAGL (WPF control) via NuGet, render nodes/edges + allow pan/zoom
- Keep styling minimal; focus on correctness + interaction

### E) Export snapshot
- Export the rendered graph to PNG:
  - default path: `WorkspaceRoot\session\exports\graph-<caseId>-<timestamp>.png`
- Log success/failure and include path in UI toast/status.

### F) Tests (DB tier)
Add deterministic SQLite tests (Infrastructure layer):
1) Graph build includes expected nodes/edges
- Seed: targets + identifiers + message presence rows
- Assert: Target↔Identifier edges exist
- Assert: Target↔Target edge weights match expected threadCount/eventCount

2) Global overlay grouping
- Seed: 2 targets in same global person, 1 target in different person
- Assert: collapsed node count and merged edge weights are correct

3) Export path builder (unit test)
- Assert file naming + directory creation is correct (no UI automation required)

### Out of Scope
- Fancy community detection, clustering algorithms, or timeline animation
- Bulk auto-linking targets/identifiers
- Multi-case “full enterprise” graph browsing (beyond global overlay)

---

## Acceptance Criteria
- Graph page loads and renders a meaningful graph for a case.
- Clicking a Target node can open Target details and can open Search filtered to that target.
- Including Identifiers shows Target↔Identifier edges.
- Group-by-global-person collapses correctly and does not lose data (weights merge).
- Export creates a PNG file and shows the path.
- `pwsh tools/test-smart.ps1 -ForceDb` passes.

---

## Manual Verify
1) Open a case with messages + linked targets/identifiers.
2) Open Association Graph:
   - verify nodes/edges render
   - click nodes and open Search filtered to them
3) Toggle Group by Global Person:
   - verify nodes collapse and edges re-weight
4) Export snapshot and open the PNG on disk.


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
