# Design Overview (Repo)

This file provides a lightweight, developer-facing overview. The authoritative design document lives in this repo as:

- `Docs/CaseGraph_Offline_Design_Doc_v2.docx` (or the latest version)

## Non-negotiable architecture decisions
- Evidence Vault is **immutable** and hashed.
- Derived/normalized data is **rebuildable** from the vault.
- Every derived record stores:
  - `SourceEvidenceItemId`
  - `SourceLocator`
  - `IngestModuleVersion`
- Significant actions are written to `AuditLog` (and may be hash-chained later).
- Identity merges are **never automatic**; they require user approval with explainable reasons.
- AI (if enabled) is **assistive only** via an online LLM gateway with RAG + redaction + citations.

## Roadmap phases (high level)
1. Vertical slice: import → normalize → search → timeline → export (with provenance)
2. Broaden ingestion: XLSX, ZIP-HTML, PLIST, geo feeds
3. Analytics: associations/graph, incident window automation, geo clustering
4. Manual Intel Registry: analyst-entered known entities and relationships
5. AI Gateway: “Ask Case” + “Next Steps” grounded in retrieved evidence
6. Hardening: performance, packaging, diagnostics, regression fixtures

## Development process
- Each change is driven by a `TICKET.md` (see `Docs/TICKETING_RULES.md`).
- Track progress in `Docs/TICKETS.md`.
- Record key decisions in `Docs/ADR.md` (append-only, reference commit IDs).
