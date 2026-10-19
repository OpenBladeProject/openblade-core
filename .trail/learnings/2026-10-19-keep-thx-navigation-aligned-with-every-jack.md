---
title: keep THX navigation aligned with every admitted audio backend
author: Jack
date: 2026-10-19
branch: feat/thx-driver-provisioning
touches:
  - src/OpenBlade.Tray/MainWindow.xaml.cs
  - tests/OpenBlade.Tray.Tests/CapabilityPresentationTests.cs
  - tests/OpenBlade.Tray.Tests/OpenBlade.Tray.Tests.csproj
---

## Decided

When adding a THX backend for another supported model, update MainWindow's production THX navigation gate as well as component admission. The reviewed installed legacy backend applies to RZ09-0482,0483,0484; their page must remain visible even when fresh provisioning is blocked. RZ09-0328 remains excluded because its factory audio is Dolby Atmos. A catalog-backed contract compares the navigation's complete model set with both V4 families and legacy candidates.

## Tried and ruled out

Disconnected THX renders bypass the production model gate, so successful legacy screenshots did not establish that a real legacy laptop could open the page. Backend/native tests also cannot detect this UI omission. Found during the cached1621 final integration review; the old gate still listed only the three V4 models.

## Watch out

Page visibility grants no write or installation authority. Keep exact backend admission, legacy spatial-only controls, model-specific component evidence, no legacy setup action, and pause-on-uncertainty behavior. Validate the actual production gate separately from simulated-render layout.
