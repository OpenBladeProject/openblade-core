---
title: Capture flyout template surfaces instead of transparent popup content
author: Jack
date: 2026-10-19
branch: fix/snaptap-info-copy
touches:
  - src/OpenBlade.Tray/App.DeveloperHarness.cs
  - src/OpenBlade.Tray/MainWindow.SnapTap.cs
  - src/OpenBlade.Tray/LocalizationSnapshotPixelCanonicalizer.cs
  - build/Capture-LocalizationSnapshots.ps1
---

## Decided
The SnapTap information capture must include WinUI's FlyoutPresenter template surface, not only Flyout.Content. RenderTargetBitmap does not capture Popup surfaces. In disconnected snapshot mode, move the existing localized content into a real FlyoutPresenter attached to the window root, apply the default or configured presenter style, capture its template root, then restore ownership in finally. Keep the private visual-test desktop and input isolation.

## Tried and ruled out
Longer layout waits do not supply missing flyout chrome. Capturing FlyoutPresenter itself returned zero pixel dimensions despite nonzero loaded layout; capturing its applied template root includes the actual background, padding and text. The earlier content-only images contained antialiased black text with zero fully opaque pixels, so varying pixel colors alone did not catch the defect.

## Watch out
Flyout captures now require both visible detail and a majority-opaque surface; rounded corners may remain transparent. General snapshots still permit transparent backgrounds. The standard localization wrapper includes laptop-snap-tap-info. These renders validate content and appearance, not native popup placement or interactive dismissal. This resolves the blank-popup limitation recorded in the earlier SnapTap copy learning without changing production input or driver admission.
