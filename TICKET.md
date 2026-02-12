# Ticket: T0002 - WPF Solution Skeleton + Professional App Shell + Theme Baseline

## Goal
Create the initial .NET/WPF solution and run-ready app skeleton with a professional-looking shell:
- Left navigation + top bar (global search + case switcher placeholders)
- Page routing to placeholder pages
- A shared design system (ResourceDictionaries for colors/typography/spacing)
- Light/Dark theme toggle (baseline)
- MVVM + DI structure so future tickets scale cleanly

## Context
We want a polished UI from day one. This ticket delivers a modern WPF shell and establishes the code structure (MVVM + DI) so the rest of the app can be built ticket-by-ticket without UI drift.

## Scope
### In scope
- Create a solution:
  - `CaseGraph.sln`
- Create projects:
  - `src/CaseGraph.App` (WPF app, .NET 8, Windows-only)
  - `src/CaseGraph.Core` (class library for domain contracts/models; minimal placeholders)
  - `src/CaseGraph.Infrastructure` (class library for shared services; minimal placeholders)
  - `tests/CaseGraph.Core.Tests` (xUnit test project; placeholder test)
- Add NuGet packages (keep minimal):
  - `Wpf.Ui` (Fluent-style navigation/theme baseline)
  - `CommunityToolkit.Mvvm` (MVVM helpers)
- Implement App Shell:
  - Left navigation items:
    - Dashboard, Import, Search, Timeline, People/Targets, Locations, Associations, Reports, Review Queue, Settings
  - Each routes to a placeholder Page with a title and short description
  - Top bar includes:
    - global search textbox (placeholder; no backend yet)
    - case selector (placeholder)
    - theme toggle (Light/Dark)
  - Right-side Evidence Drawer placeholder region (collapsed by default)
- Establish design system:
  - `Themes/Colors.xaml`
  - `Themes/Typography.xaml`
  - `Themes/Spacing.xaml`
  - `Themes/Controls.xaml` (basic common styles)
  - These are merged into `App.xaml`
- Establish DI + app startup pattern:
  - Use `Microsoft.Extensions.Hosting` Generic Host for DI/logging
  - Register ViewModels/Views/services in one place
- Update `Docs/TICKETS.md`:
  - Set Active Ticket to `T0002 ...`
  - Do NOT add to Completed Tickets yet (that happens after acceptance/commit)
- Update `Docs/ADR.md`:
  - Append a new ADR entry documenting the decision to use Wpf.Ui + MVVM toolkit
  - Use `<fill in after commit>` for commit hash

### Out of scope
- No database, no EF Core, no evidence vault, no ingest pipeline.
- No real search/timeline logic.
- No AI gateway.
- No exports.
- No external network calls (beyond normal NuGet restore during development).

## Acceptance Criteria (Testable)
- [ ] `dotnet build` succeeds from repo root.
- [ ] App runs and shows a professional shell with left nav + top bar.
- [ ] Clicking each nav item navigates to its page (placeholder content OK).
- [ ] Theme toggle switches Light/Dark without crashing.
- [ ] Design system dictionaries exist and are merged in `App.xaml`.
- [ ] Evidence Drawer placeholder region exists (can be collapsed/expanded by a button).
- [ ] `tests/CaseGraph.Core.Tests` runs with `dotnet test` (at least 1 passing test).
- [ ] `Docs/TICKETS.md` Active Ticket updated to T0002.
- [ ] `Docs/ADR.md` has a new entry for UI library/MVVM decision.

## Deliverables
- Code:
  - WPF shell + navigation + placeholders
  - DI + MVVM scaffolding
- UI:
  - Professional shell baseline using Wpf.Ui
  - Theme toggle
  - Evidence drawer placeholder
- Tests:
  - xUnit test project with 1 passing placeholder test
- Docs:
  - update `Docs/TICKETS.md` Active Ticket
  - append ADR entry

## Data Model Changes
None (no DB yet).

## Provenance & Audit Requirements
N/A for this ticket (no evidence ingestion yet).

## Performance & Resource Management Requirements
- App startup should not block UI thread.
- Avoid event-handler leaks in shell:
  - use commands/binding over manual events where possible
  - if events are used, ensure they do not create long-lived references.

## Implementation Notes
- Keep pages minimal and consistent.
- All styling should come from shared dictionaries (no random one-off styles inside pages).
- Keep dependencies minimal.

## Test Plan
- `dotnet build`
- `dotnet test`
- Manual:
  - Run app
  - Click through navigation
  - Toggle theme
  - Expand/collapse evidence drawer

## How to Verify Manually
1. From repo root: `dotnet build`
2. From repo root: `dotnet test`
3. Run: `dotnet run --project src/CaseGraph.App`
4. Verify:
   - left nav visible
   - top bar visible
   - each nav opens correct placeholder page
   - theme toggle works
   - evidence drawer expand/collapse works

## Codex Instructions
- Follow `Docs/*` rules (offline-first, UI professionalism, performance/memory).
- Implement ONLY this ticket scope.
- Keep build green; add minimal tests.
- End your response with:
  - Summary
  - Files changed
  - How to run
  - How to verify
  - Tests added + how to run
  - Repo tree for `src/`, `tests/`, and any new theme folders
