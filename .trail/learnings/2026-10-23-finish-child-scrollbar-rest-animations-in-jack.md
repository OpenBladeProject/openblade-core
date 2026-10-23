---
title: finish child scrollbar rest animations in native snapshots
author: Jack
date: 2026-10-23
branch: feat/telemetry-analysis-typography
touches:
  - build/Capture-LocalizationSnapshots.ps1
  - build/Export-LocalizationTranslatorPackage.ps1
  - src/OpenBlade.Tray/App.DeveloperHarness.cs
  - src/OpenBlade.Tray/LocalizationSnapshotPixelCanonicalizer.cs
  - tests/Build.Tests/LocalizationTranslatorWorkflow.Tests.ps1
  - tests/OpenBlade.Tray.Tests/LocalizationSnapshotPixelCanonicalizerTests.cs
  - tests/OpenBlade.Tray.Tests/WinUiShellContractTests.cs
---

## Decided
Disconnected localization captures must complete the child ScrollBar rest-state storyboards as well as select ScrollViewer NoIndicator. WinUI 2.2.1 has an empty viewer NoIndicator state; the child ScrollingIndicatorStates state fades the two-pixel panning thumb and delays collapse. Enter the native collapsed/no-indicator states and SkipToFill their current storyboards before every frame. Preserve viewport geometry, offsets, production styles, and exact same-process/fresh-process archive equality.

The v4 pixel profile additionally resolves a retained opaque achromatic filled-corner fringe using immutable quantized source, exact ring topology, and a bounded 5x5 two-surface neighborhood. Keep thin glyph and colored chart detail tests intact.

## Tried and ruled out
Viewer-only GoToState(NoIndicator, false) produced equal local PNGs but did not resolve CI40006740862: 624 pixels in the two-pixel native scrollbar thumb still differed. Local repeated PNG equality alone does not establish full translator-archive reproducibility. Additional arbitrary delays do not resolve the identified nested animation contract.

## Watch out
These are disconnected-capture fixes. Do not change installed UI styling or hide/resize scrollbar controls to stabilize a fixture. Corner normalization is deliberately restricted and does not claim to fix arbitrary chart antialiasing; all real archive exports remain required before merge.
