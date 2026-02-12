# Ticket: TXXXX - <Short Title>

## Goal
(1–3 sentences)

## Context
(Why this ticket exists / dependencies)

## Scope
### In scope
- ...

### Out of scope
- ...

## Acceptance Criteria (Testable)
- [ ] ...

## Deliverables
- Code:
- UI:
- Database (migrations):
- Tests:
- Docs:

## Data Model Changes
- Entity/Table:
  - Field: type (notes)

## Provenance & Audit Requirements
### Provenance
All derived records created must store:
- SourceEvidenceItemId
- SourceLocator (precise and stable)
- IngestModuleVersion

### Audit
Log these actions/events:
- ...

## Performance & Resource Management Requirements
- Use async/await for I/O; never block UI thread.
- Stream large files; avoid loading huge evidence blobs into memory.
- Dispose all IDisposable resources deterministically (streams/zip/db contexts).
- Avoid WPF event-handler leaks (unsubscribe or weak event patterns).
- Support CancellationToken for long operations.
- Parser stability: unknown fields skipped with logs; one bad artifact must not crash ingest.

## Implementation Notes / Edge Cases
- ...

## Test Plan
### Unit tests
- ...

### Integration tests (if applicable)
- ...

## How to Verify Manually
1. ...
2. ...

## Codex Instructions
- Implement ONLY what is in this ticket.
- Follow offline-only constraints (no network calls/telemetry).
- Preserve provenance and immutability of raw evidence.
- No silent merges.
- Keep build green; add tests; add migrations if schema changes.
- End with summary + files changed + steps to verify + tests added.
