# Ticket Index

This file tracks planned, active, and completed tickets.

## How to use
- The single source of truth for ticket execution is this file.
- Codex executes only the ticket listed in **Active Ticket** with scope defined in **Active Ticket Spec**.
- Every ticket pass must update:
  - **Upcoming Tickets** (current and deduplicated)
  - **Completed Tickets** (append-only)

## Active Ticket
- ID: (none)
- Title: (none)

## Active Ticket Spec
```md
(none)
```

## Upcoming Tickets (Planned)
- T0007 - Search (SQLite FTS5) + Search UI
- T0008 - Timeline UI + provenance drilldown
- T0009 - Basic export (timeline/keyword hits)
- T0010+ - ZIP-HTML social returns
- T0011+ - PLIST + Geo feeds
- T0015+ - Manual Intel Registry (known entities, vehicles, editable associations)
- T00A* - AI Gateway (online LLM, RAG, citations, redaction)
- T00V* - Video ingest + fast review roadmap

---

## Completed Tickets (append-only)
- 2026-02-12 - T0002 - Established WPF solution skeleton, app shell, MVVM, and DI baseline.
- 2026-02-12 - T0003 - Implemented case workspace, immutable evidence vault import, manifests, and integrity verification UI/tests.
- 2026-02-13 - T0004 - Added SQLite workspace persistence for cases/evidence, append-only audit logging, and recent activity UI.
- 2026-02-13 - T0005 - Added a persistent SQLite-backed job queue with background runner, UI job progress/cancel/history, and queue execution tests.
- 2026-02-13 - T0006 - Added message ingest (UFDR/XLSX best-effort), SQLite message schema + FTS search, queue integration, and working Search UI with provenance.
- 2026-02-13 - T0006 - Added migration-first workspace DB initialization with legacy backup/rebuild repair and startup error visibility to prevent no-window startup failures.
- 2026-02-13 - T0006 - Added startup bootstrap file logging, required-table repair with workspace.broken backups, and startup/runner DB init gating verification.
