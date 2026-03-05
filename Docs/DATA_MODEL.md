# Data Model

## Purpose
This file is the canonical high-level map of persistent data domains used by CaseGraph.

## Core domains
- Case workspace metadata:
  - Cases
  - Case-level activity summaries
- Evidence vault metadata:
  - Evidence item descriptors
  - Integrity/hash metadata
  - Source path and ingest provenance
- Communications index:
  - Threads
  - Events/messages
  - Participants and normalized identifiers
- Locations index:
  - Location observations (timestamped latitude/longitude points)
  - Source provenance fields (evidence id + locator + ingest module version)
  - Optional subject linkage (Target/Global Person)
- Target/global-person graph:
  - Targets
  - Identifiers and aliases
  - Global person linkage and association edges
- Operational observability:
  - Job queue records
  - Audit events
  - Diagnostics/session traces

## Data rules
- Every derived record must trace back to source evidence.
- Identifiers should be normalized before dedupe/search operations.
- Mutable operational state (for example jobs) must retain clear terminal outcomes.

## LocationObservation (v1)
- Table: `LocationObservationRecord`
- Key fields:
  - `LocationObservationId` (pk)
  - `CaseId`
  - `ObservedUtc`
  - `Latitude` / `Longitude`
  - `AccuracyMeters` / `AltitudeMeters` / `SpeedMps` / `HeadingDegrees` (nullable)
  - `SourceType` (`CSV|JSON|PLIST|...`)
  - `SourceLabel` (nullable)
  - `SubjectType` + `SubjectId` (nullable, no silent merges)
  - `SourceEvidenceItemId`
  - `SourceLocator` (row/json-pointer/plist key-path)
  - `IngestModuleVersion`
  - `CreatedUtc`
- Indexes:
  - `(CaseId, ObservedUtc DESC)`
  - `(CaseId, Latitude, Longitude)`
  - `(CaseId, SubjectType, SubjectId, ObservedUtc DESC)`

## Canonical references
- [Architecture](Docs/ARCHITECTURE.md)
- [Invariants](Docs/INVARIANTS.md)
- [Provenance and Audit](Docs/PROVENANCE_AND_AUDIT.md)
