UI Professionalism (WPF)
Objective

Deliver a modern, professional UI suitable for non-technical users.

Theme library decision (current default)

Default: WPF UI (Fluent-style) for navigation + theming.

Reference: https://github.com/lepoco/wpfui

Any change to the theme library must be recorded in Docs/ADR.md.

Design system requirements

Create shared ResourceDictionaries early:

Colors (surfaces, text, semantic colors)

Typography (heading/body/caption sizes)

Spacing rhythm (4/8/12/16)

Icons (consistent set)

Control styles (buttons, text fields, chips, data grids)

App shell (required)

Left navigation (primary modules)

Top bar:

global search

case switcher

incident window quick picker

Evidence Drawer pattern:

“View Source” + “Copy citation” + details

Data-heavy UI rules

Tables/grids must be readable (spacing, typography)

Clear empty states (“No results. Try widening date range.”)

Consistent row density options (Comfortable/Compact) when feasible

Consistency rule

No one-off styling inside random views. Shared styles only.
