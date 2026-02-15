# Ticket Index

This file tracks planned, active, and completed tickets.

## How to use
- The single source of truth for ticket execution is this file.
- Codex executes only the ticket listed in **Active Ticket** with scope defined in **Active Ticket Spec**.
- Every ticket pass must update:
  - **Upcoming Tickets** (current and deduplicated)
  - **Completed Tickets** (append-only)

## Active Ticket
- ID: T0009
- Title: People/Targets v1 (Manual Entry + Identifier Registry + Link from Messages)

## Active Ticket Spec
```md
# Ticket: T0009 - People/Targets v1 (Manual Entry + Identifier Registry + Link from Messages)

## Goal
Add a user-friendly **People/Targets** workflow that lets investigators:
- Manually create/edit targets (gang members / associates)
- Store and manage **identifiers** (phones, socials, emails, vehicles, etc.)
- Link message participants (sender/recipient strings) to known targets with explicit user approval
- See where an identifier appears in evidence (starting with MessageEvents)

This sets the foundation for associations/graphs and defensible “why linked” views.

## Context
We now have Messages ingest + FTS search with sender/recipient fields. We need a normalized way to:
- build a “known people” database
- track aliases/identifiers
- link evidence events to targets without silent merges

## Scope
### In scope
1) **Database schema for People/Targets + Identifiers**
2) **People/Targets UI page**
3) **Linking UI from Search → Message Detail**
4) **No silent merges**: conflicts prompt user and explain why
5) **Audit logging** for all create/edit/link actions

### Out of scope
- Association graph rendering (that’s T0010+)
- Automatic entity resolution/merging
- Contact ingest / call logs ingest / media parsing

## Acceptance Criteria (Testable)
- [ ] User can create a Target with name + aliases + notes
- [ ] User can add identifiers (phone/social/email/vehicle/etc.) to a Target
- [ ] User can edit/remove identifiers with audit trail
- [ ] From a message detail view, user can:
  - [ ] “Create Target from Sender”
  - [ ] “Link Sender to Existing Target”
  - [ ] Same for recipients
- [ ] If an identifier already exists for a different Target, UI shows a **conflict dialog**:
  - [ ] explains the conflict (“this phone is already linked to X”)
  - [ ] requires explicit user choice (cancel, move identifier, or link same target)
- [ ] `dotnet build` passes
- [ ] `dotnet test` passes (includes identifier normalization + conflict rules)
- [ ] `Docs/TICKETS.md` updated (Upcoming + Completed) and ADR appended

## Deliverables
- Code: Core models/services + Infrastructure EF migrations
- UI: People/Targets page + Message detail link actions + conflict dialogs
- Database (migrations): new tables + indexes/constraints
- Tests: unit tests for normalization/conflict + repository/service tests
- Docs: ADR entry, update tickets list

## Data Model Changes
### Entity/Table: TargetRecord
- TargetId: Guid (PK)
- CaseId: Guid (FK)
- DisplayName: string
- PrimaryAlias: string? 
- Notes: string?
- CreatedAtUtc: DateTimeOffset
- UpdatedAtUtc: DateTimeOffset
- SourceType: string  // "Manual" | "Derived"
- SourceEvidenceItemId: Guid?  // nullable for Manual
- SourceLocator: string        // required even for Manual: e.g. "manual:targets:create"
- IngestModuleVersion: string  // e.g. "manual-ui@1"

Indexes:
- (CaseId, DisplayName)

### Entity/Table: TargetAliasRecord
- AliasId: Guid (PK)
- TargetId: Guid (FK)
- CaseId: Guid
- Alias: string
- AliasNormalized: string
- SourceType: string
- SourceEvidenceItemId: Guid?
- SourceLocator: string
- IngestModuleVersion: string

Unique:
- (CaseId, AliasNormalized, TargetId)  // allow same alias on same target once

### Entity/Table: IdentifierRecord
- IdentifierId: Guid (PK)
- CaseId: Guid (FK)
- Type: string  // Phone, Email, SocialHandle, VehiclePlate, VIN, IMEI, IMSI, DeviceId, Username, Other
- ValueRaw: string
- ValueNormalized: string
- Notes: string?
- CreatedAtUtc: DateTimeOffset
- SourceType: string
- SourceEvidenceItemId: Guid?
- SourceLocator: string
- IngestModuleVersion: string

Unique:
- (CaseId, Type, ValueNormalized)

### Entity/Table: TargetIdentifierLinkRecord
- LinkId: Guid (PK)
- CaseId: Guid
- TargetId: Guid (FK)
- IdentifierId: Guid (FK)
- IsPrimary: bool
- CreatedAtUtc: DateTimeOffset
- SourceType: string
- SourceEvidenceItemId: Guid?
- SourceLocator: string
- IngestModuleVersion: string

Unique:
- (TargetId, IdentifierId)

### (Optional but recommended) Entity/Table: MessageParticipantLinkRecord
Links a MessageEvent participant string to an Identifier/Target for explainability.
- ParticipantLinkId: Guid (PK)
- CaseId: Guid
- MessageEventId: Guid (FK)
- Role: string // Sender | Recipient
- ParticipantRaw: string
- IdentifierId: Guid (FK)
- TargetId: Guid? (FK)
- CreatedAtUtc: DateTimeOffset
- SourceType: string
- SourceEvidenceItemId: Guid
- SourceLocator: string
- IngestModuleVersion: string

Indexes:
- (CaseId, IdentifierId)
- (CaseId, TargetId)
- (CaseId, MessageEventId)

## Provenance & Audit Requirements
### Provenance
For manual data:
- SourceType = "Manual"
- SourceEvidenceItemId = NULL
- SourceLocator = "manual:<page>/<action>" (required)
- IngestModuleVersion = "manual-ui@1"

For derived links from messages:
- SourceType = "Derived"
- SourceEvidenceItemId = MessageEvent.EvidenceItemId
- SourceLocator = MessageEvent.SourceLocator + role info (e.g., "...;role=Sender")
- IngestModuleVersion = "targets-linker@1"

### Audit
Log these actions/events:
- TargetCreated / TargetUpdated / TargetDeleted (if allowed)
- AliasAdded / AliasRemoved
- IdentifierCreated / IdentifierUpdated / IdentifierRemoved
- IdentifierLinkedToTarget / IdentifierUnlinkedFromTarget
- ParticipantLinked (message->identifier/target)
Each audit entry should include CaseId + TargetId/IdentifierId when applicable.

## Performance & Resource Management Requirements
- Use async/await for I/O; never block UI thread.
- Use short-lived DbContexts; prefer IDbContextFactory for UI-driven reads.
- Keep queries indexed (Identifier uniqueness by (CaseId, Type, ValueNormalized)).
- CancellationToken supported for long-running loads (e.g., targets list with paging).
- Dispose resources deterministically.

## Implementation Notes / Edge Cases
- Normalization:
  - Phone: strip non-digits; if 10 digits assume US and normalize to +1XXXXXXXXXX; if already starts with + keep E.164-like form
  - Email: trim + lowercase
  - Social handle: trim + lowercase; preserve leading @ in raw but not required in normalized
  - Plate/VIN: uppercase + remove spaces/hyphens
- Conflict dialog:
  - Trigger when inserting IdentifierRecord violates unique constraint OR when attempting to link to a different target.
  - Present options:
    1) Cancel
    2) Link existing identifier to this target (only if user confirms move/unlink from other target or allow multi-target if policy allows)
  - Default is Cancel (defensive).
- No silent merges:
  - Never auto-create targets from participants; always user-driven.
- UI should be simple:
  - Left list of Targets (searchable)
  - Detail panel with Aliases + Identifiers + Notes
  - “Add Alias” / “Add Identifier” buttons
  - “Where seen” section (initially: message count and a link to filtered search)

## Test Plan
### Unit tests
- Identifier normalization for each type
- Conflict behavior when identifier already exists
- Linking creates correct provenance fields (manual vs derived)

### Integration tests (if applicable)
- EF migration creates unique constraints
- Service methods enforce constraints and return friendly errors

## How to Verify Manually
1. Run app, open a case with messages ingested
2. Go to People/Targets → create a target “Test Target”
3. Add phone identifier “(555) 123-0001”
4. Search messages and open a message where sender is +15551230001
5. Click “Link Sender to Existing Target” → pick “Test Target”
6. Confirm target “Where seen” shows message count > 0
7. Attempt to create a new target and add the same phone → conflict dialog appears

## Codex Instructions
- Follow offline-only constraints (no network calls/telemetry).
- Preserve provenance and immutability of raw evidence.
- No silent merges.
- Update Docs/TICKETS.md Upcoming + Completed at end of pass.
- Append ADR entry documenting schema + normalization + conflict policy.
- End with summary + files changed + steps to verify + tests run.
```

## Upcoming Tickets
- T0010 - Association graph v1 and relationship visualization (pending spec authoring).

## Completed Tickets (append-only)
- 2026-02-12 - T0002 - Established WPF solution skeleton, app shell, MVVM, and DI baseline.
- 2026-02-12 - T0003 - Implemented case workspace, immutable evidence vault import, manifests, and integrity verification UI/tests.
- 2026-02-13 - T0004 - Added SQLite workspace persistence for cases/evidence, append-only audit logging, and recent activity UI.
- 2026-02-13 - T0005 - Added a persistent SQLite-backed job queue with background runner, UI job progress/cancel/history, and queue execution tests.
- 2026-02-13 - T0006 - Added message ingest (UFDR/XLSX best-effort), SQLite message schema + FTS search, queue integration, and working Search UI with provenance.
- 2026-02-13 - T0006 - Added migration-first workspace DB initialization with legacy backup/rebuild repair and startup error visibility to prevent no-window startup failures.
- 2026-02-13 - T0006 - Added startup bootstrap file logging, required-table repair with workspace.broken backups, and startup/runner DB init gating verification.
- 2026-02-14 - T0007 - Completed message ingest/search v1 hardening with platform-filtered cancellable search, deterministic XLSX threading, UFDR/XLSX guidance statuses, and deterministic tests.
- 2026-02-14 - T0008 - Added live parse progress/status UX, queued/running cancel reliability, sender/recipient search filters, and unified app/job debug logging with deterministic tests.
- 2026-02-14 - T0008A - Fixed terminal job overwrite semantics, race-safe cancel handling, and fresh no-tracking UI job reads so completed jobs show 100% and final status immediately.
- 2026-02-15 - T0008B - Enforced final non-throttled terminal overwrite in runner, with cancellation-safe save and deterministic success/cancel/failure finalize tests.
- 2026-02-15 - T0008C - Added Evidence Drawer live MessagesIngest job syncing with fresh no-tracking refresh, terminal-aware cancel visibility, and manual refresh control.
- 2026-02-15 - T0009 - Added People/Targets v1 schema, UI workflows, message participant linking, explicit conflict handling, audits, and deterministic tests.
