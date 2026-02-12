# Ticket: T0001 - Repo Bootstrap, Docs System, and Development Guardrails

## Goal
Bootstrap the repository correctly before any app code by creating the Docs system, ticketing rules, Codex instructions, UX/UI standards, performance/memory rules, offline + provenance + audit requirements, and VS Code/.NET baseline configuration.

## Scope
In scope:
- Create folders: Docs/, src/, tests/, fixtures/, .vscode/
- Create root config + docs: README.md, .gitignore, .editorconfig, Directory.Build.props, global.json
- Create VS Code configs: .vscode/extensions.json, settings.json, tasks.json, launch.json
- Create Docs files: DESIGN.md, TICKETING_RULES.md, TICKET_TEMPLATE.md, TICKETS.md, ADR.md, CODEX_GUIDE.md, CODEX_BASE_PROMPT.md, PROVENANCE_AND_AUDIT.md, SOURCE_LOCATOR_CONVENTIONS.md, PERFORMANCE_AND_MEMORY.md, AI_GATEWAY_RULES.md, USABILITY_GUIDELINES.md, UI_PROFESSIONALISM.md, FIXTURES_AND_TEST_DATA.md

Out of scope:
- No application code, no solution/projects, no dependencies, no CI, no installer.

## Acceptance Criteria
- [ ] All folders exist: Docs/, src/, tests/, fixtures/, .vscode/
- [ ] All root files exist: README.md, .gitignore, .editorconfig, Directory.Build.props, global.json
- [ ] All .vscode files exist: extensions.json, settings.json, tasks.json, launch.json
- [ ] All Docs files exist and match intended content.
- [ ] Docs/TICKETS.md includes “Completed Tickets (append-only)” at the end.
- [ ] Docs/ADR.md is append-only and includes a template referencing Ticket + Commit hash.

## How to Verify
- Confirm repo tree matches expected structure.
- Open Docs files and ensure they are non-empty and consistent.

## Codex Instructions
- Create/overwrite only the files described by this ticket.
- Do not add application code or dependencies.
- End with a repo tree and list of created files.
