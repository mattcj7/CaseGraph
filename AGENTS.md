# CaseGraph Agent Rules

## Ticket Source Of Truth
- Tickets live only in `Docs/TICKETS.md`.
- Do not create root-level ticket files (`TICKET.md`, `TICKETS.md`, etc.).
- Implement only the ticket listed under **Active Ticket** unless explicitly redirected.

## Required Repo Updates Per Ticket Run
- Keep **Upcoming Tickets** current and deduplicated.
- Append a completion line in **Completed Tickets** when acceptance criteria are met.
- When architecture or reliability behavior changes, append/update `Docs/ADR.md`.

## Verification
- Default verification command: `pwsh tools/test-smart.ps1`.
- Do NOT run root `dotnet test` unless the active ticket explicitly includes `FULL_TESTS_REQUIRED` or the user requests a full suite.
- Full suite commands: `pwsh tools/test-smart.ps1 -Full` or `pwsh tools/test-full.ps1`.
- Keep the selected verification tier(s) green; if not possible, report exact failures and blockers.

## DB Tier Triggers
- DB tier must run when changes touch workspace init, SQLite configuration, `DbContext`, migrations, ingest pipeline, or query services.
- Trigger examples include `WorkspaceDatabaseInitializer`, `WorkspaceMigrationService`, `Workspace*Db*`, `ServiceCollectionExtensions` (when configuring `UseSqlite`), `TimeoutWatchdog`, persistence/migrations/jobs/ingest/parsers, and `DbContext` files.
- DB tier also runs when diffs include SQLite/EF migration keywords such as `UseSqlite`, `SqliteConnectionStringBuilder`, `Database.Migrate`, `__EFMigrationsHistory`, `DefaultTimeout`, or `busy_timeout`.
- Use `pwsh tools/test-smart.ps1`; do not skip DB tier when these triggers match. Use `-ForceDb` to force DB tier explicitly.

## Reliability and Diagnostics
- Prefer structured logging with correlation IDs and scoped context.
- Ensure global exception paths log full stack traces and correlation IDs.
- Keep logs redacted: no evidence bodies or raw PII unless explicitly required.

## Offline and Provenance Constraints
- Assume offline/local-first operation for core workflows.
- Preserve immutable evidence and audit provenance behavior.
- Do not introduce hidden data mutation paths that bypass audit trails.
