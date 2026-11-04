---
title: Localize XAML properties through one shared resource loader
author: Jack
date: 2026-11-04
branch: fix/codebase-reliability-audit
touches:
  - CHANGELOG.md
  - build/Extract-WinUiLocalization.ps1
  - build/Validate-LocalizationResources.ps1
  - docs/localization.md
  - src/OpenBlade.Tray/App.DeveloperHarness.cs
  - src/OpenBlade.Tray/App.xaml.cs
  - src/OpenBlade.Tray/LocalizedResources.cs
  - src/OpenBlade.Tray/MainWindow.xaml
  - src/OpenBlade.Tray/OpenBlade.Tray.csproj
  - src/OpenBlade.Tray/TrayDashboardWindow.xaml
---

## Decided
Resolve compiled localized XAML properties with the public CustomXamlResourceLoader and {CustomResource existing.resw.key}. Register once on the UI thread before any localized view. Reuse LocalizedResources and its cached Resources map; embed the authoritative English resw as the fallback catalog rather than duplicating strings. Keep canonical keys and translator formats unchanged. Normalize dotted resource paths for both explicit-context and default ResourceLoader reads, preserving namespace brackets.

The matched fixture grew MRM ResourceMapSubtree allocations from 9 to 409 over 200 x:Uid recreations; CustomResource stayed at 3 with 406 retired references collected and all translation/accessibility/tooltip/fallback checks passing. The migrated Main and Dashboard then completed 70 changes, collected all 293 retired references, passed attachment rollback, and held MRM at 3 across eight checkpoints. These are disconnected private-desktop controls, not installed-product or visible GPU/energy validation.

## Tried and ruled out
Official Windows App SDK 2.6.5 still retained 101 maps after 101 public-API lookups and manager destruction. Upgrading alone is not a fix. A custom C# MarkupExtension avoided MRM but retained five 120-byte native MarkupExtension wrappers per fixture; do not substitute that leak. Static x:Bind functions in DataTemplates did not initialize with a null DataContext; adding dummy data would hide the correctness problem.

## Watch out
CustomResource keys must remain validated against the canonical catalog and the destination property, including attached-property namespace syntax. Update extraction and translation validation together; never silently skip markup and drop keys. Preserve separate raw-source and resolved-English layout tests. Windows PowerShell must decode UTF-8 resources explicitly. Native header censuses are sampled allocation-leading pointers on the default heap, not all native memory. Private bytes and some Event handles still grow; small shared composition pools and transient XAML allocations remain. Do not call the whole native-memory problem solved. No private native frees, forced COM releases, production GC, or runtime dependency patches are needed for this mitigation.
