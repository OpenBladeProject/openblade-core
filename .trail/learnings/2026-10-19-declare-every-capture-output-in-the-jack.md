---
title: Declare every capture output in the translator screenshot map
author: Jack
date: 2026-10-19
branch: fix/snaptap-info-copy
touches:
  - build/localization/TranslatorScreenshotMap.json
  - build/Capture-LocalizationSnapshots.ps1
  - tests/Build.Tests/LocalizationTranslatorWorkflow.Tests.ps1
---

## Decided
Every PNG emitted by a new localization capture page must be declared in TranslatorScreenshotMap.json in the same change. The SnapTap information route emits both tray/main-laptop-snap-tap-info.png and tray/snap-tap-information.png. Map SnapTap resource keys to those images so translators see the relevant feature.

## Tried and ruled out
The full solution tests and isolated popup review passed, but the translator export correctly rejected the new undeclared PNG. Listing the page in Capture-LocalizationSnapshots.ps1 alone is incomplete. Keep strict screenshot-set admission; add the authoritative declarations and context rule rather than accepting arbitrary images.

## Watch out
Run LocalizationTranslatorWorkflow.Tests.ps1 after changing capture output. Its real package exports exercise the map and compare same-process and fresh-process ZIP bytes. The map regression checks SnapTap_InfoInput.Text points to both real images. The corrected workflow passed all three byte-identical exports.
