---
title: "Cooling-pad fan owners and the MainWindow x:Bind converter crash"
author: Jack
date: 2026-11-21
branch: fix/cooling-pad-razer-driver-stack
cost: { minutes: 29, tokens: 223226 }
touches:
  - CHANGELOG.md
  - docs/ui-typography.md
  - src/OpenBlade.Tray.Core/CoolingPadViewModel.cs
  - src/OpenBlade.Tray.Core/Properties/Resources.qps-ploc.resx
  - src/OpenBlade.Tray.Core/Properties/Resources.resx
  - src/OpenBlade.Tray.Core/Properties/Resources.ru-RU.resx
  - src/OpenBlade.Tray.Core/Properties/Resources.zh-CN.resx
  - src/OpenBlade.Tray/AppTypography.cs
  - src/OpenBlade.Tray/MainWindow.GlobalSearchCatalog.cs
  - src/OpenBlade.Tray/MainWindow.xaml
---

## Decided
- The RC21-0230 pad keeps two fan records in class 0D: selector 0 is owned by the firmware, selector 1 by Synapse.
  - 0D/90 echoes the selector. It replies with 3 bytes for speed presets 0-2 and 2 bytes otherwise.
  - Read back the owner you wrote; selector-0 readback of a Synapse write never matches.
  - 0D/81 is the ramping target. It matches a new demand only within 50 RPM, so it cannot confirm a 0D/01 write.
- MainWindow color controls use {Binding ... Converter={StaticResource RgbColor}}, as the keyboard lighting card does. Flyout content does not inherit DataContext, so the ColorPicker sets its own.

## Tried and ruled out
- x:Bind with Converter={StaticResource ...} in MainWindow.xaml. The generated LookupConverter reads the Window root's Resources, misses the root Grid's converter, and crashes startup with 0xC000027B (stowed E_FAIL in combase / Microsoft.UI.Xaml.dll). A contract test in CoolingPadViewModelTests now rejects x:Bind converters there.

## Watch out
- Harness smoke runs crash before the tray registers crash reporting, so no crash report or smoke report is written.
  - To see the XAML error, temporarily hook App.UnhandledException plus FirstChanceException at the top of OnLaunched, then rebuild with -p:OpenBladeDeveloperHarness=true.
- Localization tooling requires each CustomResource key's suffix to match the XAML property it feeds (.Text, .Header, ...AutomationProperties.Name). One key cannot serve both a caption Text and a NumberBox AutomationProperties.Name.
- Do not run tray smoke tests while a broker install is running. One broker install rolled back while smoke trays were starting; the retry with nothing else running succeeded.
