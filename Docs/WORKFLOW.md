# Workflow

## Verification Default
- Use `pwsh tools/test-smart.ps1` for normal ticket verification.
- Do NOT run root `dotnet test` unless the ticket explicitly includes `FULL_TESTS_REQUIRED` or the user asks for full-suite validation.

## Tier Definitions
- FAST tier (`tools/test-fast.ps1`):
  - Runs every time.
  - Builds solution once, then runs fast core tests with `--no-build`.
- DB tier (`tools/test-db.ps1`):
  - Runs when changed files indicate persistence/query/ingest risk.
  - Trigger patterns:
    - `src/**/Persistence/**`
    - `src/**/Migrations/**`
    - `src/**/Jobs/**`
    - `src/**/Ingest/**`
    - `src/**/Parsers/**`
    - filenames containing `DbContext` or `WorkspaceDbContext`
- UI tier (`tools/test-ui.ps1`):
  - Runs when changed files indicate WPF/resources/startup risk.
  - Trigger patterns:
    - `**/*.xaml`
    - `src/**/Themes/**`
    - `src/**/Resources/**`
    - `src/**/Views/**`
    - `src/**/App.xaml*`
    - `src/**/MainWindow*`
- FULL tier (`tools/test-full.ps1`):
  - Runs only for `FULL_TESTS_REQUIRED` or explicit full-suite requests.

## Smart Runner Behavior
- `tools/test-smart.ps1` always executes FAST tier.
- It inspects both:
  - `git diff --name-only`
  - `git diff --name-only --staged`
- It prints which tiers run, and why, including triggering files.
- It avoids redundant build/restore loops by reusing build output for follow-on tiers (`--no-build --no-restore`).

## Commands
- Default: `pwsh tools/test-smart.ps1`
- Force full suite: `pwsh tools/test-smart.ps1 -Full`
- Run a specific tier:
  - `pwsh tools/test-fast.ps1`
  - `pwsh tools/test-db.ps1`
  - `pwsh tools/test-ui.ps1`
  - `pwsh tools/test-full.ps1`
