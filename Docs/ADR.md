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
