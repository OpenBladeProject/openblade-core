---
title: Conflicting-controller (Synapse) notice wording
author: Jack
date: 2026-11-25
branch: fix/conflicting-controller-messaging
cost: { minutes: 22, tokens: 226693 }
touches:
  - CHANGELOG.md
  - Directory.Build.props
  - src/OpenBlade.Tray.Core/MainViewModel.cs
  - src/OpenBlade.Tray.Core/Properties/Resources.qps-ploc.resx
  - src/OpenBlade.Tray.Core/Properties/Resources.resx
  - src/OpenBlade.Tray.Core/Properties/Resources.ru-RU.resx
  - src/OpenBlade.Tray.Core/Properties/Resources.zh-CN.resx
  - src/OpenBlade.Tray/Strings/en-US/Resources.resw
  - src/OpenBlade.Tray/Strings/qps-ploc/Resources.resw
  - src/OpenBlade.Tray/Strings/ru-RU/Resources.resw
---

## Decided
The tray names Synapse when any of RazerAppEngine, Razer Synapse Service, RazerSynapseService or razerwdl is in PotentialControllers (MainViewModel.SynapseControllers), and FormatControllerAdvisory picks the wording: Synapse or other software, controls usable or blocked (unverified or WritesBlocked). Troubleshooting keeps the raw names for diagnostics. PR #1011.

## Tried and ruled out
Saying "your OpenBlade changes still apply" when writes are blocked is false, so the blocked state gets its own wording. Mentioning Synapse Blade Blocker in the UI was dropped: the service aliases still count as controllers even with a verified patch, so the notice can persist.

## Watch out
Tray.Core Resources*.resx are hand-maintained: Update-LocalizationResources.ps1 regenerates only the en-US resw and pseudo locales, so add the en, ru and zh entries yourself. The old advisory format strings had no resx entries and were never translated. In Windows PowerShell 5.1 scripts, ’ (U+2019) ends a single-quoted string, so use a placeholder. Git Bash sed -i can strip CRLF, so renormalize after it.
