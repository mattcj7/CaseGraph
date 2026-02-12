# CaseGraph Offline

CaseGraph Offline is a **single-machine, offline Windows desktop** application (C#/.NET + WPF) for ingesting and analyzing **lawfully obtained** digital evidence (Cellebrite UFDR + Excel exports, zipped HTML social media returns, plists, geo feeds, and other files). It accelerates gang/homicide investigations via **unified search**, **timelines**, **geolocation analytics**, **association graphs**, and **court-ready exports**.

## Non-negotiable constraints
- **Offline core:** no cloud dependencies, no telemetry, no external network calls for core features.
- **Provenance-first:** every derived record must store `SourceEvidenceItemId`, `SourceLocator`, and `IngestModuleVersion`.
- **Auditability:** significant actions (imports, merges, exports, AI queries) must be logged.
- **No silent merges:** identity/entity merges require explicit review and approval.
- **AI is assistive only:** AI outputs are annotations with citations; never modifies evidence or auto-merges identities.
- **Usability:** designed for non-technical users with guided workflows and consistent navigation.
- **Performance:** streaming I/O, deterministic disposal, cancellation support, no UI thread blocking.

## Major modules (planned)
- Evidence Vault (immutable) + hashing
- Ingest connectors (UFDR, XLSX, ZIP-HTML, PLIST, CSV/JSON geo, etc.)
- Normalized database + full-text search
- Timeline + Incident Window tools
- Locations/Geo analysis
- Associations/Graph analysis
- Manual Intel Registry (analyst-entered known data)
- Reports/Exports (with citations)
- Optional AI Gateway (online LLM only; RAG; redaction; citations)

## How we work (ticket-based)
- Every development step is defined in a **single `TICKET.md`** (authoritative for the change).
- `Docs/TICKETS.md` tracks planned and completed tickets.
- `Docs/ADR.md` records key decisions and changes (append-only, reference commit IDs).

## Documentation
- Design overview: `Docs/DESIGN.md`
- Ticketing rules: `Docs/TICKETING_RULES.md`
- Ticket template: `Docs/TICKET_TEMPLATE.md`
- Codex guide and base prompt: `Docs/CODEX_GUIDE.md`, `Docs/CODEX_BASE_PROMPT.md`
- Provenance & audit: `Docs/PROVENANCE_AND_AUDIT.md`
- Source locator conventions: `Docs/SOURCE_LOCATOR_CONVENTIONS.md`
- Performance/memory rules: `Docs/PERFORMANCE_AND_MEMORY.md`
- AI gateway rules: `Docs/AI_GATEWAY_RULES.md`
- Usability & UI professionalism: `Docs/USABILITY_GUIDELINES.md`, `Docs/UI_PROFESSIONALISM.md`
- Fixtures/test-data strategy: `Docs/FIXTURES_AND_TEST_DATA.md`

## Repository layout
- `Docs/` — standards, rules, templates, and design notes
- `src/` — application source code (later tickets)
- `tests/` — test projects (later tickets)
- `fixtures/` — sanitized fixture structures and sample formats (no sensitive content)
