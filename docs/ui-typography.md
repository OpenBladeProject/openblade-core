# UI typography

OpenBlade uses the native WinUI type ramp through application-level semantic
styles. The framework continues to choose the system font, language fallback,
high-contrast colors, and text-scale behavior. Do not set a custom font family
or fixed line height on application text.

The current WinUI base styles resolve to an expected 28-DIP title, 20-DIP
section title, 14-DIP body, and 12-DIP caption scale. Those values describe the
native ramp; component code should reference the semantic styles rather than
copy their current sizes.

| Role | Style | Use |
| --- | --- | --- |
| Page title | `AppPageTitleTextStyle` | One title at the start of a page |
| Section title | `AppSectionTitleTextStyle` | Major groups within a page |
| Body | `AppBodyTextStyle` | Descriptions, table cells, and dialog text |
| Strong body | `AppBodyStrongTextStyle` | Card titles and table column headers |
| Caption | `AppCaptionTextStyle` | Units, timestamps, chart axes, and supporting metadata |
| Data | `AppDataTextStyle` | Numeric readings and table values that benefit from tabular figures |
| Command | `AppCommandButtonStyle` | String page commands with wrapping, centered text and a 40-DIP minimum height |

All table body cells use regular weight, including the selected reading, range
statistics and counts. Reserve strong body text for headers. Data cells at the
same level use the same data role. Supporting units and timestamps use
captions; the selected timestamp may use strong body text to identify the active
inspection. `AppDataTextStyle` uses tabular
numerals so changing values remain aligned.

Let text wrap when translations or text scaling need more room. Avoid fixed row
heights around text, ad hoc `FontSize` values, and clipping to preserve a
one-line layout. Use theme resources for text color so light, dark, and high
contrast themes retain their native behavior. Runtime-created text uses
`AppTypography.Apply`; runtime-created buttons can resolve
`AppTypography.CommandButton` with `AppTypography.RequireStyle`.
The command style uses `AppCommandTextTemplate` for string content. Buttons
whose content is an icon or custom element keep their component-specific style.

The tray quick-controls popup retains its existing compact 12-DIP and 11-DIP
exceptions for now. Any new exception needs a constrained surface and a render
check at increased text scale. New page and telemetry UI use the application
roles above.
