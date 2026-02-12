# Codex Guide

## Purpose
Codex is used to implement tickets. The ticket (`TICKET.md`) is the source of truth.

## Hard constraints (must follow)
- Offline core only (no external network calls, no telemetry).
- Provenance-first: every derived record includes:
  - `SourceEvidenceItemId`, `SourceLocator`, `IngestModuleVersion`
- No silent merges: entity merges require explicit approval with reasons.
- AI is assistive only and stored as annotations; must cite evidence IDs.

## What you give Codex
1. Ensure the repo contains `Docs/*` rules.
2. Create/update the repo root `TICKET.md`.
3. Paste the base prompt from `Docs/CODEX_BASE_PROMPT.md`.

## Codex output contract (must appear at end of every run)
Codex must end its response with:
- Summary of changes
- Files added/modified
- How to build/run
- How to verify manually (click path in UI if applicable)
- Tests added/updated + how to run them
- Notes/assumptions

## Do not list
- Do not broaden scope beyond the ticket.
- Do not add network calls (HTTP clients, telemetry, analytics).
- Do not auto-merge identities.
- Do not store raw evidence content in memory longer than necessary.
- Do not bypass provenance requirements.

## References
- Ticket rules: `Docs/TICKETING_RULES.md`
- Ticket template: `Docs/TICKET_TEMPLATE.md`
- Provenance & audit: `Docs/PROVENANCE_AND_AUDIT.md`
- Source locators: `Docs/SOURCE_LOCATOR_CONVENTIONS.md`
- Performance/memory: `Docs/PERFORMANCE_AND_MEMORY.md`
- AI gateway: `Docs/AI_GATEWAY_RULES.md`
- Usability: `Docs/USABILITY_GUIDELINES.md`
- UI professionalism: `Docs/UI_PROFESSIONALISM.md`
