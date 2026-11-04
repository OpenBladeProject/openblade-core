# Native resource validation summary

The detailed investigation is preserved at immutable commit
[`0ba5840e`](https://github.com/OpenBladeProject/openblade-core/tree/0ba5840e45fa6ba80183ab1a2c9c397295f3b99f/build/benchmarks/NativeResources),
retained by branch `chore/reliability-audit-evidence-20261104`.
The [full report](https://github.com/OpenBladeProject/openblade-core/blob/0ba5840e45fa6ba80183ab1a2c9c397295f3b99f/build/benchmarks/NativeResources/RESULTS.md) and its linked JSON/CSV
files contain the historical controls, exact binary/source hashes and limitations.
Those reports describe different measured revisions; they are not all measurements
of the final application build.

## Confirmed fixes

| Defect | Retained implementation | Evidence |
| --- | --- | --- |
| Generated XAML event delegates retained retired views | Detach generated subscriptions and stop bindings during terminal teardown | [Matched managed-lifetime control](https://github.com/OpenBladeProject/openblade-core/blob/0ba5840e45fa6ba80183ab1a2c9c397295f3b99f/build/benchmarks/NativeResources/results/2026-11-03-lifetime-comparison.json): baseline 14/14 views retained; candidate 0/14 |
| Recreating native shells and backdrops accumulated native resources | Keep shell, Mica, icon and native subscriptions across language changes | [Localized shell comparison](https://github.com/OpenBladeProject/openblade-core/blob/0ba5840e45fa6ba80183ab1a2c9c397295f3b99f/build/benchmarks/NativeResources/results/2026-11-04-localized-shell-retirement.json): all 297 retired references collected; Composition/Timer stayed at 13/15 |
| AppWindow.Changed retained native argument objects | Shared shell-owned size constraints use the supported native window messages | [Sizing fix](https://github.com/OpenBladeProject/openblade-core/blob/0ba5840e45fa6ba80183ab1a2c9c397295f3b99f/build/benchmarks/NativeResources/results/2026-11-04-window-sizing-native-fix.json): all three argument groups absent through 58 language changes |
| Canceled Closing events retained native argument objects | Shared WM_CLOSE interception preserves close-to-tray behavior | [Close/hide control](https://github.com/OpenBladeProject/openblade-core/blob/0ba5840e45fa6ba80183ab1a2c9c397295f3b99f/build/benchmarks/NativeResources/results/2026-11-04-window-closing-subscription-control.json) and [fix](https://github.com/OpenBladeProject/openblade-core/blob/0ba5840e45fa6ba80183ab1a2c9c397295f3b99f/build/benchmarks/NativeResources/results/2026-11-04-window-closing-native-fix.json): 200 cycles; all three groups absent with the fix |
| Per-element x:Uid resource lookups retained native MRM subtrees | One public CustomXamlResourceLoader uses the existing map and canonical English fallback | [Compiled control](https://github.com/OpenBladeProject/openblade-core/blob/0ba5840e45fa6ba80183ab1a2c9c397295f3b99f/build/benchmarks/NativeResources/results/2026-11-04-mrm-customresource-control.json): 9 to 409 subtrees in baseline; 3 throughout with the shared loader |

The [integrated localization run](https://github.com/OpenBladeProject/openblade-core/blob/0ba5840e45fa6ba80183ab1a2c9c397295f3b99f/build/benchmarks/NativeResources/results/2026-11-04-mrm-localized-view-fix.json)
completed 70 language changes and attachment rollback, collected all 293 retired
references, and kept MRM at 3 across eight complete censuses. Previously fixed
Changed/Closing argument groups stayed absent. Locale, accessibility, tooltip and
fallback checks passed. The embedded English catalog preserves the existing
translation keys and translator format.

## Remaining limits

Native memory is not fully resolved. In the integrated run, private bytes rose
by 217.83 MB, default-heap busy payload by 28.44 MB and retained free capacity by
166.12 MB. These sequential, overlapping observations are not additive ownership
accounting. Events rose from 415 to 456 and settled at 452; a 128-256 KiB allocation
bucket still grew. Earlier Intel-associated allocator references do not establish
a driver leak or a supported application cleanup operation.

Runs used disconnected developer harnesses on unswitched private desktops,
with uncontrolled host load and OS caches. Managed collection, native allocation
counts, private bytes and kernel handles measure different things. These controls
do not establish visible-window GPU residency, installed power consumption,
hardware behavior or multi-hour stability. Production adds no forced GC,
private COM releases, native-handle forcing or heap-optimization calls.

## Reproduction and review

Use the [harness instructions](README.md) for new bounded measurements. Preserve
new detailed reports outside implementation PRs and link the exact source and
artifact snapshot from a concise summary. Existing historical paths under
`results/` resolve through the [archive index](results/README.md).

The combined source at the archive commit passed 11,386 solution tests across
17 projects, 2,668 Release Tray tests, localization workflow checks and six
English/Russian/pseudo-localized rendered views. Extracted PRs must report their
own checks separately. Earlier Claude reviews cover preceding revisions; the
latest requested native review was incomplete because of a weekly quota limit.
