# Codex Base Prompt (paste this into Codex every time)

```text
Implement the repository’s root TICKET.md exactly as written.

Rules:
- Offline core only: no external network calls or telemetry.
- Preserve provenance for every derived record: SourceEvidenceItemId + SourceLocator + IngestModuleVersion.
- Raw evidence must be immutable in the evidence vault.
- No silent merges (all merges require explicit review/approval with reasons).
- Use performant, safe .NET practices: async I/O, streaming for large files, deterministic disposal, cancellation tokens.
- WPF: avoid event-handler memory leaks (unsubscribe or use weak events when needed).
- Follow usability and UI professionalism rules in Docs/USABILITY_GUIDELINES.md and Docs/UI_PROFESSIONALISM.md.

Deliverables:
- Keep build green; add migrations if schema changes; add/update tests.
- End with: summary of changes, files touched, how to verify manually, tests added and how to run them.
