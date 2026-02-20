# Codex Prompt Template

Use this template when assigning a ticket implementation run.

## Verification Policy (required block)
- Do NOT run `dotnet test` at repo root by default.
- Run `pwsh tools/test-smart.ps1`.
  - FAST tier always runs.
  - DB and UI tiers run only when changed files match their routing rules.
- Run full suite only when the ticket explicitly says `FULL_TESTS_REQUIRED` or the user requests it:
  - `pwsh tools/test-smart.ps1 -Full`
  - or `pwsh tools/test-full.ps1`
- Prefer `--no-build`/`--no-restore` paths to avoid restore/rebuild loops.

## Prompt Skeleton
```text
Implement Active Ticket <ID> from Docs/TICKETS.md.

SOURCE OF TRUTH:
- Tickets live ONLY in Docs/TICKETS.md.
- Implement ONLY the active ticket scope.

Verification policy:
- Default: pwsh tools/test-smart.ps1
- Do NOT run root dotnet test unless FULL_TESTS_REQUIRED or user explicitly requests full suite.

When finished, provide:
- Summary
- Files changed
- Commands to verify
- Which tiers ran and why
```
