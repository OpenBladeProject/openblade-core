---
title: Recheck Windows mode evidence before returning refresh state
author: Jack
date: 2026-11-10
branch: feat/rust-windows-display-refresh
touches:
  - rust/crates/openblade-platform-windows/src/display/refresh.rs
  - rust/crates/openblade-platform-windows/src/display/refresh/edid.rs
---

## Decided
Accept exactly the bounded count of GDI driver modes by checking the next index for the documented stop signal. Preserve skipped, unusable, and native-failed timing reads separately. Compare GDI current mode and selected CCD observations before returning the refresh projection. Fail checked refresh overflow as the managed reader does.

## Tried and ruled out
Use identical decoded timing tuples for cross-language policy tests. Exercise binary EDID decode separately and compare every decoded field. An interlaced timing and an EDID ratio that cannot fit the native rational are separate reasons to exclude a Dynamic candidate.

## Watch out
The rechecks detect observed GDI or CCD changes. They cannot make the Windows calls atomic. GDI enumeration FALSE does not carry a documented end-versus-failure status. Keep partial enumerations out when the count bound is exceeded.
