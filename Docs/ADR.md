# Architecture / Decision Record (ADR)

Append-only log of key decisions and changes. Add an entry when we:
- adopt/replace a major library or architecture approach
- change provenance/audit rules
- change storage/indexing strategy
- change AI gateway approach
- change UX/navigation standards

## Entry template
### ADR-YYYYMMDD-XX: <Title>
- Date: YYYY-MM-DD
- Ticket: TXXXX
- Commit: <hash>
- Decision:
  - <what we decided>
- Rationale:
  - <why>
- Consequences:
  - <trade-offs, follow-up work>
- Alternatives considered:
  - <optional>

---

## Entries
### ADR-20260211-01: Project guardrails and documentation system
- Date: 2026-02-11
- Ticket: T0001
- Commit: <fill in after commit>
- Decision:
  - Establish ticket-driven development with Codex, provenance-first and offline core rules.
- Rationale:
  - Prevent drift and ensure defensibility and maintainability from day one.
- Consequences:
  - All future work must follow `Docs/*` standards and use `TICKET.md` as scope authority.
- Alternatives considered:
  - Ad-hoc development without formal tickets (rejected).

### ADR-20260212-02: WPF shell baseline with WPF-UI and MVVM toolkit
- Date: 2026-02-12
- Ticket: T0002
- Commit: <fill in after commit>
- Decision:
  - Adopt WPF-UI for shell/theming and CommunityToolkit.Mvvm with Generic Host DI for scalable app composition.
- Rationale:
  - Establishes a professional UI baseline with centralized dependency registration before feature implementation.
- Consequences:
  - Future tickets should use the shared theme dictionaries and MVVM+DI patterns introduced in T0002.
- Alternatives considered:
  - Plain WPF styling with ad-hoc service wiring (rejected).

### ADR-20260212-03: Case workspace root and immutable evidence vault layout
- Date: 2026-02-12
- Ticket: T0003
- Commit: <fill in after commit>
- Decision:
  - Store workspace data under `%LOCALAPPDATA%\CaseGraphOffline\cases\<CaseId>\` with `case.json` and `vault\<EvidenceItemId>\{original\...,manifest.json}`.
- Rationale:
  - Provides an offline, deterministic, and defensible file layout where imported evidence is copied once and treated as immutable.
- Consequences:
  - Import and verification workflows are now path-driven and reconstructable from `case.json` and per-item manifests.
- Alternatives considered:
  - Mutable shared file references without vault copy (rejected).

### ADR-20260213-04: SQLite workspace index with EF Core and path provider abstraction
- Date: 2026-02-13
- Ticket: T0004
- Commit: <700ff9b>
- Decision:
  - Use `workspace.db` (SQLite via EF Core) as the persistent index for cases, evidence metadata, and append-only audit events.
  - Introduce `IWorkspacePathProvider` to centralize workspace path resolution and enable temp-root injection in tests.
- Rationale:
  - Enables offline persistence/queryability across restarts while keeping evidence vault manifests as integrity source-of-truth.
  - Improves test reliability by avoiding hard dependency on machine `LocalAppData`.
- Consequences:
  - Workspace services now depend on DB initialization and path provider abstractions.
  - Evidence/case loading and recent activity UI are backed by SQLite records.
- Alternatives considered:
  - Continue file-only indexing in `case.json` without a relational index (rejected).

### ADR-20260213-05: Persistent job queue with BackgroundService runner and channel dispatch
- Date: 2026-02-13
- Ticket: T0005
- Commit: <d2efab7>
- Decision:
  - Persist background jobs in SQLite `JobRecord` rows and execute them through `JobRunnerHostedService`.
  - Use an in-memory `Channel<Guid>` dispatcher over persisted queued jobs, with `IObservable<JobInfo>` updates to the UI.
- Rationale:
  - Long-running ingest and verification operations must survive app restarts, support cancellation, and avoid UI thread blocking.
  - Persisted job state plus startup abandonment handling makes execution history auditable and recoverable.
- Consequences:
  - Import and verify actions are now asynchronous queue requests rather than direct UI-thread operations.
  - Job lifecycle events (`JobQueued`, `JobStarted`, `JobSucceeded/Failed/Canceled`) are now audit-logged.
  - Review Queue now surfaces recent job history and payload/error details for analysts.
- Alternatives considered:
  - In-memory-only task execution without persisted job state (rejected due to restart and audit gaps).

### ADR-20260213-06: Message schema and FTS5 trigger sync for communications search
- Date: 2026-02-13
- Ticket: T0006
- Commit: <fill in after commit>
- Decision:
  - Introduce normalized message ingest tables (`MessageThreadRecord`, `MessageEventRecord`, `MessageParticipantRecord`) with required provenance (`SourceLocator`, `IngestModuleVersion`).
  - Use SQLite FTS5 (`MessageEventFts`) with insert/update/delete triggers on `MessageEventRecord` to keep the search index synchronized.
  - Execute UFDR/XLSX parsing as a queued `MessagesIngest` background job with progress, cancellation, and audit summaries.
- Rationale:
  - Message-first investigations require fast local search and defensible provenance tied to immutable evidence.
  - Trigger-based synchronization avoids drift between canonical message rows and search index rows.
  - Queue execution keeps parsing off the UI thread and preserves operational history.
- Consequences:
  - Ingest uses deterministic delete-and-rebuild per evidence item to enforce idempotency.
  - Search uses FTS `MATCH` with snippet extraction and falls back to LIKE on malformed queries.
  - Startup schema bootstrap now ensures message tables, FTS table, and triggers exist even on pre-T0006 workspaces.
- Alternatives considered:
  - Storing messages as opaque JSON blobs without normalized/search tables (rejected).
  - Rebuilding FTS entirely in application code after each ingest (rejected in favor of trigger-based consistency).

### ADR-20260213-07: Migration-first workspace DB initialization with legacy backup and rebuild
- Date: 2026-02-13
- Ticket: T0006
- Commit: <fill in after commit>
- Decision:
  - Switch startup DB initialization to migration-first (`Database.MigrateAsync`) with explicit detection of legacy DBs that lack `__EFMigrationsHistory`.
  - For legacy DBs, perform automatic backup (`workspace.legacy.<timestamp>.db`), create a fresh migrated DB, and rebuild cases/evidence from on-disk `case.json` and vault `manifest.json`.
  - Gate startup/job runner DB access behind a shared workspace initializer and surface initialization failures via modal startup error dialog.
- Rationale:
  - Existing installs created via non-migration paths can miss new tables (for example `JobRecord`) and brick startup without clear user feedback.
  - Automatic backup + rebuild preserves evidence metadata continuity while safely recovering to a known schema.
- Consequences:
  - Legacy job history is not rebuilt and is intentionally discarded during repair.
  - Startup now fails visibly with actionable diagnostics instead of silent background process hangs.
  - Infrastructure tests now cover legacy-repair backup, required-table migration, and rebuild-from-manifest behavior.
- Alternatives considered:
  - Continue `EnsureCreated` plus ad-hoc `CREATE TABLE IF NOT EXISTS` patches (rejected due to schema drift and missing migration history).
  - Require manual user DB deletion when startup fails (rejected due to operator friction and risk of data confusion).

### ADR-20260213-08: Startup bootstrap logging and required-table DB self-heal
- Date: 2026-02-13
- Ticket: T0006
- Commit: <fill in after commit>
- Decision:
  - Add a bootstrap startup file logger that writes to `%LOCALAPPDATA%\CaseGraphOffline\logs\app-YYYYMMDD.log` before DI/host startup.
  - Update workspace DB initialization to verify required tables via `sqlite_master` and repair broken DBs by backing up to `workspace.broken.<timestamp>.db`, then re-running migrations and rehydrating case/evidence from disk manifests.
  - Enforce startup ordering so the workspace initializer runs before host start/background queue activity, while preserving startup exception MessageBox visibility.
- Rationale:
  - Startup failures must be diagnosable even when host/service wiring fails early.
  - Migration history checks alone are insufficient when required tables are missing or DB state is partially broken.
  - Initialization ordering must prevent queue/job queries from touching an unhealed schema.
- Consequences:
  - Broken DB backups now use `workspace.broken.*` naming and legacy job history remains intentionally non-rehydrated.
  - App startup now emits deterministic on-disk logs for both successful steps and startup failures.
  - Additional deterministic infrastructure test coverage now validates legacy DB repair during runner startup.
- Alternatives considered:
  - Rely only on host logging and migration-history presence checks (rejected due to blind spots during early startup and partial schema corruption).

### ADR-20260214-09: Messages ingest/search v1 contract hardening and operator guidance
- Date: 2026-02-14
- Ticket: T0007
- Commit: <fill in after commit>
- Decision:
  - Keep the normalized message schema and SQLite FTS5 trigger-sync model, while hardening v1 behavior with platform-filtered search service contracts and cancellable UI search execution.
  - Standardize ingest outcome guidance for no-sheet XLSX and unsupported/encrypted UFDR paths so `MessagesIngest` jobs complete with actionable operator messages.
  - Make XLSX thread-key fallback deterministic via v1 hash derivation from platform/sender/recipients when conversation IDs are absent.
- Rationale:
  - Analysts need predictable query filtering and non-blocking UX when searching large message corpora.
  - No-result ingest paths must be explicit and actionable to avoid silent operator ambiguity.
  - Deterministic thread keys improve rebuild-per-evidence idempotency and cross-run consistency.
- Consequences:
  - `IMessageSearchService` now accepts platform filters directly, and search cancellation is wired through the app view model.
  - `MessagesIngest` status text now differentiates actionable no-data cases from successful extraction counts.
  - Deterministic ingest tests now cover platform filtering and guidance statuses for unsupported UFDR / non-message XLSX exports.
- Alternatives considered:
  - Keep platform filtering only in UI post-processing and generic no-data status messages (rejected due to weaker contract clarity and operator feedback).

### ADR-20260214-10: Messages ingest UX/cancel hardening with structured filters and unified file logging
- Date: 2026-02-14
- Ticket: T0008
- Commit: <fill in after commit>
- Decision:
  - Extend the existing file logger into a shared app-wide logger (`app-YYYYMMDD.log`) and instrument job runner, queue lifecycle, cancel requests, and messages ingest checkpoints.
  - Keep cancellation control in the persistent queue service with per-running-job CTS tracking, while ensuring queued jobs are immediately marked canceled and running jobs are canceled promptly.
  - Extend message search filtering strategy to combine FTS query matching with structured optional sender/recipient substring filters.
- Rationale:
  - Operators need actionable diagnostics for stalls, cancellations, and lifecycle transitions without adding another logging framework.
  - Queue-centered cancellation preserves deterministic DB state transitions and immediate UI status updates.
  - Analysts need sender/recipient narrowing without sacrificing FTS relevance for primary query terms.
- Consequences:
  - Parse progress/status now surfaces `%` plus processed counters (`X / Y`) through live job updates.
  - Cancel handling and job lifecycle behavior are now covered with deterministic queued/running cancellation tests.
  - Search contracts and UI now include sender/recipient filters, with SQL-level narrowing and matching tests.
- Alternatives considered:
  - Introducing a separate structured logging framework for queue/ingest flows (rejected to avoid competing logging systems and added complexity).

### ADR-20260214-11: Terminal job-state overwrite and fresh no-tracking UI reads
- Date: 2026-02-14
- Ticket: T0008A
- Commit: <fill in after commit>
- Decision:
  - Normalize all terminal job transitions (`Succeeded`, `Failed`, `Canceled`, `Abandoned`) to persist `CompletedAtUtc`, `Progress=1.0`, and terminal `StatusMessage` values.
  - Compose `MessagesIngest` success terminal messages in the runner from handler output counts, with optional handler summary override text for guidance scenarios.
  - Keep UI job refreshes backed by fresh `DbContextFactory` queries with `AsNoTracking`, and harden running-cancel races via pending-cancel signaling until per-job CTS registration is available.
- Rationale:
  - Jobs that are terminal in status but partial in progress/message create “hung” operator perception and break trust in queue state.
  - Terminal messaging must come from deterministic runner-level completion rules, independent of progress callbacks.
  - Race-safe cancel guarantees predictable queued vs running behavior under rapid operator actions.
- Consequences:
  - Terminal job rows now converge on consistent completion fields, including queued cancels and app-shutdown abandonments.
  - `MessagesIngest` completion now reliably surfaces `Succeeded: ...` summaries with 100% progress.
  - Additional deterministic tests now cover terminal overwrite semantics, queued-cancel terminal fields, and fresh-read query behavior.
- Alternatives considered:
  - Preserve partial progress for canceled/failed jobs (rejected to avoid ambiguous “still in progress” UX in terminal states).

### ADR-20260215-12: Runner-final terminal overwrite in finally with non-cancel save token
- Date: 2026-02-15
- Ticket: T0008B
- Commit: <fill in after commit>
- Decision:
  - Enforce a single terminal overwrite in `JobQueueService.ExecuteAsync` `finally` for all runner-completed terminal outcomes (`Succeeded`, `Failed`, `Canceled`), setting `CompletedAtUtc`, `Progress=1.0`, terminal `StatusMessage`, and `ErrorMessage` as applicable.
  - Cancel linked job tokens before final persistence, then write terminal state with `CancellationToken.None` to guarantee save even after cancellation is requested.
  - Publish a final `JobUpdate` event only after the terminal overwrite save succeeds.
- Rationale:
  - Asynchronous progress callbacks can race with completion and leave terminal jobs with stale mid-run `Progress`/`StatusMessage` values.
  - Terminal persistence must be deterministic and cancellation-safe so UI and audits always observe terminal truth.
- Consequences:
  - Final terminal fields are now overwritten as the last runner write, preventing post-completion progress regressions.
  - Failed jobs now retain full exception strings in `ErrorMessage` while `StatusMessage` carries a concise `Failed: ...` summary.
  - Deterministic infrastructure tests now assert success/cancel/failure terminal finalize invariants.
- Alternatives considered:
  - Relying on per-path completion helpers without a final unconditional overwrite (rejected due to callback race windows).

### ADR-20260215-13: People/Targets v1 schema, normalization, and explicit conflict policy
- Date: 2026-02-15
- Ticket: T0009
- Commit: <fill in after commit>
- Decision:
  - Add normalized target/identifier persistence with `TargetRecord`, `TargetAliasRecord`, `IdentifierRecord`, `TargetIdentifierLinkRecord`, and `MessageParticipantLinkRecord`.
  - Enforce identifier uniqueness by `(CaseId, Type, ValueNormalized)` and target-identifier uniqueness by `(TargetId, IdentifierId)`.
  - Implement manual-vs-derived provenance fields on target/identifier/link records, with derived message links carrying message evidence provenance.
  - Require explicit user conflict resolution for identifier collisions (`Cancel`, `MoveIdentifierToRequestedTarget`, `UseExistingTarget`) with cancel-default behavior.
- Rationale:
  - Investigative workflows need a defensible, queryable people registry with auditable linkage from message participants to known targets.
  - Normalization and uniqueness constraints prevent accidental duplicates while preserving explicit operator control for conflict cases.
  - Provenance and explicit conflict choices support explainability and no-silent-merge requirements.
- Consequences:
  - People/Targets UI can create/edit targets, aliases, identifiers, and link message participants with explicit approvals.
  - Audits now include target create/update, alias changes, identifier create/update/remove/link/unlink, and participant link events.
  - New deterministic tests cover normalization, conflict behavior, and derived-link provenance.
- Alternatives considered:
  - Implicit auto-merge on identifier collisions (rejected due to defensibility and false-link risk).

### ADR-20260218-14: Workspace DB migration-on-open with fatal diagnostics containment
- Date: 2026-02-18
- Ticket: T0009C
- Commit: <fill in after commit>
- Decision:
  - Standardize workspace schema upgrades on app startup and case open/create flows by using EF Core migrations (`Database.MigrateAsync`) through a shared workspace migration service.
  - Add app-level fatal exception handling across `DispatcherUnhandledException`, `AppDomain.CurrentDomain.UnhandledException`, and `TaskScheduler.UnobservedTaskException` with FATAL log entries, crash dialog diagnostics, and clean shutdown.
  - Add a diagnostics UI surface showing workspace/log paths, version metadata, and last log lines with copy/open actions.
  - Add smoke coverage for old DB fixtures, old-case open behavior, and `CaseGraph.App --self-test` process execution.
- Rationale:
  - Legacy workspaces can miss newer schema tables and must be upgraded in-place before Open Case reads.
  - Fatal failures need actionable operator diagnostics (correlation ID, stack trace, log location) instead of silent exits.
  - Regression protection requires real SQLite fixture upgrades and process-level startup checks.
- Consequences:
  - Existing workspaces are migrated instead of relying on `EnsureCreated`, reducing old-db open crashes.
  - Unexpected UI/background exceptions now produce deterministic fatal logs and user-facing crash diagnostics before shutdown.
  - Diagnostics visibility and self-test automation improve operational triage and release confidence.
- Alternatives considered:
  - Continue ad-hoc table checks or `EnsureCreated`-first initialization (rejected due to migration drift risk).
