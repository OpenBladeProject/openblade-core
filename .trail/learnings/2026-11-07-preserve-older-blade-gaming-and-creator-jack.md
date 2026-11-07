---
title: Preserve older Blade Gaming and Creator modes across handback
author: Jack
date: 2026-11-07
branch: feat/official-source-reviewed-support
touches:
  - src/OpenBlade.Protocol/ExperimentalBladePort.Fans.cs
  - src/OpenBlade.Protocol/ExperimentalBladeModels.cs
  - src/OpenBlade.Contracts/Profiles.cs
  - src/OpenBlade.Tray.Core/PerformancePresetPresentation.cs
---

## Decided
RZ09-0300/0246 and RZ09-0351/0268 expose distinct typed Gaming and Creator presets with raw values 1 and 2, plus Balanced 0. They have no Custom mode. Preserve the exact mapping in decoding, writes and Automatic fan handback, and throughout optional Windows power-mode preferences, shortcuts, notifications and serialization. New RZ09-0330/0253 allows Balanced and Custom CPU/GPU 0 through 2.

## Tried and ruled out
Do not call raw 1 Turbo or raw 2 Maximum Power on the older Base family. Merely changing preset SET and GET would leave the former generic Automatic-handback Performance mapping at raw 2, incorrectly changing Gaming into Creator. Vendor SetGPUValue side effects are outside the admitted thermal HID contract. Do not inherit the adapter-query failure fallback: fresh successful 07/8C and matching native power remain required.

## Watch out
RZ09-0330 Boost 3 has an explicit EC prerequisite with narrower vendor variant scope; no admitted typed EC query currently proves it, so leave Boost unavailable. Actual Windows DMI can omit a year suffix or a full model token; use the exact observed string together with its reviewed PID. Product 587 middleware navigation is stale and points to RZ09-0301, whereas its guide identifies RZ09-0313. Unconfirmed Pro and Advanced product identities remain pending. Firmware shortlinks can disappear: record unavailable vendor artifacts honestly and do not substitute another variant carrier.
