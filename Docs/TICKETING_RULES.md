# Ticketing Rules

## Principle
All work is ticket-driven. A ticket is the unit of scope, review, and delivery.

## Workflow
1. Create/update `TICKET.md` (authoritative scope and acceptance criteria).
2. Run Codex against `TICKET.md` following `Docs/CODEX_GUIDE.md`.
3. Review changes locally:
   - build
   - tests
   - manual verification steps
4. If accepted:
   - commit with a clear message referencing the ticket ID
   - append the ticket to **Completed Tickets** in `Docs/TICKETS.md`
   - add an ADR entry in `Docs/ADR.md` if a key decision was made

## Ticket ID format
- `T0001`, `T0002`, ... sequential.

## Required ticket structure
Use `Docs/TICKET_TEMPLATE.md` verbatim.

## Definition of Done (every ticket)
- Builds successfully (no broken solution state).
- Long operations do not block UI thread.
- No network calls introduced (offline core).
- Deterministic disposal of `IDisposable` resources.
- CancellationToken supported for long jobs (ingest/index/search).
- Provenance populated on all derived records:
  - `SourceEvidenceItemId`, `SourceLocator`, `IngestModuleVersion`
- Audit entries recorded for significant actions (imports, merges, exports, AI queries).
- Parser tolerance: unknown fields skipped with structured logs; one bad artifact does not crash whole ingest.
- Tests added/updated for critical behavior and invariants.
- Ticket includes clear manual verification steps.
