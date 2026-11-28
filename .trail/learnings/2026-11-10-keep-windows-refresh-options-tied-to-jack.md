---
title: Keep Windows refresh options tied to mode evidence
author: Jack
date: 2026-11-10
branch: feat/rust-windows-display-refresh
touches:
  - rust/crates/openblade-platform-windows/src/display/refresh.rs
  - "src/OpenBlade.Display/WindowsDisplaySettingsController.cs#BuildRefreshOptions"
---

## Decided
Phase 6c projects the ordinary managed v2 QueryDisplay refresh fields from the selected active CCD path, its source's GDI modes, and target-bound preferred mode and EDID evidence. Keep the CCD desktop rate, physical timing, boost bit, and virtual mode flag separate. Verify the Rust policy with shared offline cases evaluated by the managed display policy.

## Tried and ruled out
Do not use the GDI current frequency or the preferred timing to classify the current CCD pair. Fixed options need a driver mode plus advertised, current GDI, or physical evidence. A 60/240 split without boost has no current key. Dynamic choices use a visible desktop rate and compatible target timing; a raw EDID range alone does not establish a mode.

## Watch out
Indexed EnumDisplaySettingsExW uses FALSE as its stop signal. A current-mode FALSE is unavailable, and a bound hit discards the partial enumeration. GDI and EDID reads occur after the CCD frame. This projection is a Windows adapter leaf; full QueryDisplay still needs HDR, color, brightness and authorized Session Agent integration.
