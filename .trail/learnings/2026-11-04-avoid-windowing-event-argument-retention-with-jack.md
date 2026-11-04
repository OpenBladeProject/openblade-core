---
title: Avoid Windowing event-argument retention with shell-owned size constraints
author: Jack
date: 2026-11-04
branch: fix/codebase-reliability-audit
touches:
  - CHANGELOG.md
  - build/benchmarks/NativeResources/RESULTS.md
  - src/OpenBlade.Tray/App.DeveloperHarness.cs
  - src/OpenBlade.Tray/MainWindow.xaml.cs
  - src/OpenBlade.Tray/WindowContentHost.cs
  - tests/OpenBlade.Tray.Tests/TrayXamlStartupSmokeTests.cs
  - tests/OpenBlade.Tray.Tests/WinUiShellContractTests.cs
  - build/benchmarks/NativeResources/results/2026-11-04-windowing-change-subscription-control.json
  - src/OpenBlade.Tray/App.WindowSizeSmoke.cs
  - src/OpenBlade.Tray/WindowsWindowSizeConstraints.cs
---

## Decided
Replace MainWindow.AppWindow.Changed sizing with one WindowsWindowSizeConstraints owned by the permanent WindowContentHost. The no-subscription control removed all three sampled native ChangedArgs families; the actual fix kept them absent at six checkpoints over 58 language changes, reclaimed245retired references and held Composition13/Timer15. See NativeResources/results/2026-11-04-windowing-change-subscription-control.json and window-sizing-native-fix.json. This removes the observed304payload bytes per change, not all native growth.

The exact InteractiveExperiences2.0.15 projection exposes public OverlappedPresenter preferred minimum/maximum properties. Use those for proactive limits, and native window messages for DPI/work-area refresh and restored-window reconciliation. Keep the reentrancy guard across clearing, DefSubclassProc and reapplication even if a clear setter fails. Clear the cached tuple before setters; publish it after success. Tuple equality skips setters, not current-geometry enforcement. Keep helper lifetime with the shell, never localized views.

## Tried and ruled out
XML-only API inspection incorrectly missed the available presenter properties. Pinned WinUI2.2.1 lacks newer Window.MinWidth/Height APIs. Window.SizeChanged alone can miss unchanged logical-size DPI transitions. The first control reused a stale runner and produced no heap files; exclude it. UTF16 marker checks must consider both byte alignments or parse metadata.

Maximized outer bounds include invisible resize borders. Checking raw outer width against work-area width falsely failed2582 versus2560pixels. Do not resize maximized/minimized windows to compensate; retain native state and restoration geometry. Native smoke checks client bounds, restored size, changed limits while minimized, monitor identity, same-DPI suggested undersizing and native destruction. Iterate the pinned DisplayArea vector by Count/index; its IEnumerable projection failed QI in the isolated runtime.

## Watch out
Private bytes still rose176136192to388198400 in the fix census. The separate MRM subtree leak and Intel-linked allocator growth remain unresolved. HeapWalk covers the default heap; absence at checkpoints is not proof of zero transient allocation or indefinite stability. Raw addresses, dumps and private paths stay local. Temporary instrumentation is removed before ordinary tests and delivery. Claude's requested new review remains incomplete after its weekly quota failure; the independent Codex review is not a Claude review.
