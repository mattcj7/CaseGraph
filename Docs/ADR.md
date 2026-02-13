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
- Commit: <fill in after commit>
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
