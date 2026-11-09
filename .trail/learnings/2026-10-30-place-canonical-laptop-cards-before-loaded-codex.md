---
title: place canonical laptop cards before Loaded
author: Codex
date: 2026-10-30
branch: feat/keyboard-input-backends
touches:
  - CHANGELOG.md
  - src/OpenBlade.Tray/MainWindow.xaml
  - src/OpenBlade.Tray/MainWindow.xaml.cs
  - tests/OpenBlade.Tray.Tests/WinUiShellContractTests.cs
---

## Decided

Keep canonical Power, Fans and Battery ContentControls in LaptopCardsHost from XAML construction onward, in the same final order and using the same ViewModel and dashboard templates. Dashboard copies keep their separate template-backed presentations. Remove the initial Home-to-Laptop relocation from Root.Loaded, and ignore Loaded callbacks after MainWindow disposal.

## Tried and ruled out

After main refresh, an existing live-localization smoke failed first on its unchanged eight-second phase budget, then a serial full-suite run produced COM 0x800F1000 at UIElementCollection.Insert in MoveCanonicalLaptopCardsIntoPlace. Both parents contained the same late cross-panel relocation. A passing isolated Tray run and successful CI quality run did not erase that concrete crash.

Do not diagnose missing Windows components from this generic HRESULT, swallow COM exceptions, or lengthen the smoke deadlines. A disposed guard alone cannot explain a fresh replacement window's first load. The direct XAML placement removes the failing native operation. rootLoaded already uses asynchronous continuations; preserve its existing completion ordering.

## Watch out

Verify actual initial parent, order, Content binding and template identity in the semantic shell contract. Keep the existing localization rollback/success smoke unchanged so it exercises real replacement windows, then validate the final head. The trace's timestamps include synchronous invocation, UI-dispatch scheduling and cleanup; they cannot by themselves identify one blocked await. This shared UI fix applies across models and does not change input ownership, firmware admission or hardware scope.
