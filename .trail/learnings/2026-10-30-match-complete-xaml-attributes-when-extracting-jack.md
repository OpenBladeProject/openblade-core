---
title: Match complete XAML attributes when extracting localized text
author: Jack
date: 2026-10-30
branch: fix/section-drag-preview
touches:
  - build/Extract-WinUiLocalization.ps1
  - build/Validate-LocalizationResources.ps1
  - src/OpenBlade.Tray.Core/Properties/Resources.qps-ploc.resx
  - src/OpenBlade.Tray.Core/Properties/Resources.resx
  - src/OpenBlade.Tray.Core/Properties/Resources.ru-RU.resx
  - src/OpenBlade.Tray.Core/Properties/Resources.zh-CN.resx
  - src/OpenBlade.Tray/Strings/en-US/Resources.resw
  - src/OpenBlade.Tray/Strings/qps-ploc/Resources.resw
  - src/OpenBlade.Tray/Strings/ru-RU/Resources.resw
  - src/OpenBlade.Tray/Strings/zh-CN/Resources.resw
---

## Decided
Managed TrayText labels need their own authoritative Resources.resx entries even when identical wording already exists in a WinUI resw catalog. Reuse reviewed translations when moving that wording into the managed catalog. Match complete XAML attribute names in both extraction and validation so local CardReorder.IsHeader and NavigationView.AlwaysShowHeader remain boolean layout metadata, while real Header, Text and supported accessibility/tool-tip properties are localized.

## Tried and ruled out
Unbounded property-name regexes matched the Header suffix inside IsHeader and AlwaysShowHeader. Regeneration then created Header=True entries and preserved an older navigation Header=False entry. Do not translate those boolean strings or add their bogus keys to language catalogs. Use an attribute-name boundary and remove the obsolete generated key from every locale.

## Watch out
Run the combined catalog updater, validator and complete Build.Tests contract set; ordinary dotnet solution tests do not run these PowerShell contracts. Keep an output-based extraction fixture that proves native Text/Header survive and attached IsHeader does not become text. Regeneration must remain byte-idempotent.
