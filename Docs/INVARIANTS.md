# Invariants

These rules are stable guardrails for all CaseGraph ticket work.

## Product invariants
- Offline-first behavior is required for core workflows.
- Imported evidence is immutable after vault copy.
- Derived records must retain provenance to source evidence.
- Audit events are append-only; no silent state mutation.

## Engineering invariants
- Implement only the active ticket scope unless explicitly redirected.
- Keep changes deterministic and local; no network dependency for core behavior.
- Prefer async I/O for file and process operations.
- Keep validation messages actionable (what failed, where, and how to fix).

## Canonical references
- [Architecture](Docs/ARCHITECTURE.md)
- [Data Model](Docs/DATA_MODEL.md)
- [Ticket Context Workflow](Docs/Ticket_Context_Workflow.md)
- [Existing ADR log](Docs/ADR.md)
