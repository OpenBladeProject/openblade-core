---
title: Preserve live CPU observation and editor recovery semantics
author: Jack
date: 2026-11-07
branch: feat/cpu-voltage-completion
touches:
  - docs/rz09-0528-cpu-tuning.md
  - src/OpenBlade.Service/ServiceControlRuntime.CpuTuning.cs
  - src/OpenBlade.Tray.Core/CpuTuningViewModel.cs
  - src/OpenBlade.Tray.Core/Properties/Resources.qps-ploc.resx
  - src/OpenBlade.Tray.Core/Properties/Resources.resx
  - src/OpenBlade.Tray.Core/Properties/Resources.ru-RU.resx
  - src/OpenBlade.Tray.Core/Properties/Resources.zh-CN.resx
  - src/OpenBlade.Tray/App.DeveloperHarness.CpuTuning.cs
  - tests/OpenBlade.Service.Tests/CpuTuningRuntimeTests.cs
  - tests/OpenBlade.Tray.Tests/CpuTuningViewModelTests.cs
---

## Decided
Keep the live CPU editor synchronized with confirmed uniform measurements while it is clean. Mixed or unavailable measurements invalidate clean Apply until an explicit choice. Preserve dirty intent, queued request snapshots, and the remembered negative offset when Off reports zero.
A saved-only CPU observation retains the last live presentation and recovery journal status without granting fresh write authority. Treat Blocked/None as an availability denial for either live or legacy firmware observation; explain confirmed legacy recovery separately.
## Tried and ruled out
Do not infer owned recovery from Blocked alone. Do not clear live recovery when a request intentionally skips that backend. Do not emit unchanged CPU properties and command events on every global status refresh.
## Watch out
Continuity revocation across Service, power and sleep remains intentional. Ordinary OwnedVerified and Blocked journals are excluded from the current narrow read-only post-boot settlement path. Broader settlement needs separate contract review and failure tests; preserving diagnostics is not proof that original hardware cleanup completed. Claude reviewed an earlier exact snapshot and selected paths; Codex validated follow-up fixes separately.
