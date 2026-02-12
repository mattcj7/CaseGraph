Provenance & Audit
Principle

Everything derived must be traceable back to a source artifact. Reports must be defensible.

Required provenance fields (all derived records)

SourceEvidenceItemId — the imported evidence container/file reference

SourceLocator — a stable pointer into that source (see Docs/SOURCE_LOCATOR_CONVENTIONS.md)

IngestModuleVersion — version string for the parser/connector

Audit logging expectations

Log these actions (minimum):

Evidence import started/completed (counts, errors, skipped artifacts)

Entity merge proposed/approved/rejected

Report/export generated

AI query executed (if AI gateway enabled)

Rebuild/reindex actions (when implemented)

Evidence-derived vs Analyst-entered data

The system supports both:

Evidence-derived records:

MUST include provenance fields.

Analyst-entered Intel:

MUST be clearly labeled in UI and storage.

MUST have attribution (entered by/at) and a source note (IntelSource concept).

Must be fully audited with change history.

Reporting rule

Exports must include citations:

at minimum: EventId (or equivalent), SourceEvidenceItemId, SourceLocator
