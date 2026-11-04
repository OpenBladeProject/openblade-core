---
title: Preserve native shells and WinRT event projections across localization
author: Jack
date: 2026-11-04
branch: fix/codebase-reliability-audit
touches:
  - CHANGELOG.md
  - build/benchmarks/NativeResources/Program.cs
  - build/benchmarks/NativeResources/README.md
  - build/benchmarks/NativeResources/RESULTS.md
  - src/OpenBlade.Tray/App.DeveloperHarness.cs
  - src/OpenBlade.Tray/App.GpuStartupHarness.cs
  - src/OpenBlade.Tray/App.LiveReorderSmoke.cs
  - src/OpenBlade.Tray/App.ResourcePerformanceHarness.cs
  - src/OpenBlade.Tray/App.ResourceWindowPresentation.cs
  - src/OpenBlade.Tray/App.xaml.cs
---

## Decided
Keep the main and quick-controls native Window shells and their WindowContentHost roots across language changes; replace only localized UserControls. Keep Mica and the invariant icon with the native shell. Borrow existing native brightness/performance subscriptions during the replacement transaction and resolve the active main view from a method-group callback. Retired views must not close or hide their successor, and failed attachment must restore both previous views.

Cache each view's HostWindow.AppWindow projection for its complete event-subscription lifetime. Reacquiring the projection for -= can consult a different C#/WinRT event-state table: native delegate routes retained retired MainWindow.Changed/Closing and TrayDashboardWindow.Closing handlers. Caching the subscribing projection reclaimed all tracked retired views and stopped Timer growth. A notification lambda also shared a compiler closure retaining the original view.

## Tried and ruled out
Stable windows and a permanent content root alone did not stop Composition growth while Mica was recreated. Keeping Mica per shell held Composition at13. Reapplying SetIcon on each attachment caused GDI/USER growth; assigning it only for a new shell removed that trend. Clearing content/DataContext and moving diagnostic GC to a worker did not fix retained views. Empty ordinary gcroot results did not exclude native callback roots.

The exact recorded Microsoft.UI.Input/dcompi10.0.27200.1024 binary path frees LocalContentOutput while its LayoutSync owning reference remains. SystemVisualTarget.Close did run; the final count was one and its Composition handle remained open. Nonvirtual-base deletion is a source-level inference, not source proof. See the sanitized reference ledger in NativeResources/results.

## Watch out
The71-change disconnected run collected297retired weak references with Composition13/Timer15flat and GDI99→99/USER79→77. Private memory still increased; these results do not establish native allocation stability, installed power, visible presentation or indefinite behavior. Keep raw dumps/addresses local and publish sanitized evidence with binary hashes and limits. Never add private COM releases, forced native-handle closure, binary patching or production GC.

MainWindow and TrayDashboardWindow now inherit UserControl even though their names are preserved. Use HostWindow for actual Window APIs and ownership. Generated UserControl Loading delegates need detachment as well as StopTracking. Snapshot input guards now apply to the permanent content host; private-desktop synthetic hit-testing tests must reenable that ancestor explicitly while preserving native no-activate guards.
