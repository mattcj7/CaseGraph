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
- Run `dotnet test` before marking a ticket complete.
- Keep build and tests green; if not possible, report exact failures and blockers.

## Reliability and Diagnostics
- Prefer structured logging with correlation IDs and scoped context.
- Ensure global exception paths log full stack traces and correlation IDs.
- Keep logs redacted: no evidence bodies or raw PII unless explicitly required.

## Offline and Provenance Constraints
- Assume offline/local-first operation for core workflows.
- Preserve immutable evidence and audit provenance behavior.
- Do not introduce hidden data mutation paths that bypass audit trails.
