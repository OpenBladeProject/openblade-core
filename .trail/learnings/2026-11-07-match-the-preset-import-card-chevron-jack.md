---
title: Match the preset import card chevron
author: Jack
date: 2026-11-07
branch: feat/labs-sidebar
touches:
  - src/OpenBlade.Tray/MainWindow.xaml
  - tests/OpenBlade.Tray.Tests/WinUiShellContractTests.cs
---

## Decided
Use the same trailing chevron on the Lighting preset import card and the Labs guide cards. The import card keeps its beaker beside the title and opens the existing file picker.

## Tried and ruled out
The file-action glyph at the right edge looked out of place on the full-width card. It still fits the labeled telemetry toolbar action.

## Watch out
Keep the trailing cue decorative and preserve the import button's accessible name and availability binding.
