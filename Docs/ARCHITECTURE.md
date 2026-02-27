# Architecture

## System shape
CaseGraph is a local-first desktop application with layered responsibilities:
- `CaseGraph.App`: WPF UI, interaction orchestration, operator workflows.
- `CaseGraph.Core`: domain contracts, invariants, and shared models.
- `CaseGraph.Infrastructure`: SQLite persistence, ingest/search/query services, and background job execution.

## Key runtime boundaries
- UI must not directly mutate persistence; writes flow through service abstractions.
- Workspace state persists in local SQLite and filesystem vault structures.
- Long-running operations run off the UI thread and support cancellation.

## Ticketing and change boundaries
- Ticket work is scoped by `Docs/TICKETS.md`.
- Context packs for new tickets are created under `Docs/Tickets/`.
- Decision records are captured in `Docs/ADR.md` (historical) and `Docs/DECISIONS/` (per-ticket stubs/templates).

## Canonical references
- [Invariants](Docs/INVARIANTS.md)
- [Data Model](Docs/DATA_MODEL.md)
- [Workflow](Docs/Ticket_Context_Workflow.md)
