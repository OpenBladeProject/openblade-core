---
title: Bind THX API admission to real software components and protected service state
author: Jack
date: 2026-10-20
branch: feat/thx-driver-provisioning
touches:
  - AGENTS.md
  - CHANGELOG.md
  - docs/blade18-source-feature-recovery.md
  - docs/razer-brightness-driver-package.md
  - docs/ru-RU/razer-brightness-driver-package.md
  - docs/snap-tap.md
  - docs/thx-audio.md
  - docs/thx-installation-investigation.md
  - docs/thx-legacy-interface.md
  - docs/zh-CN/razer-brightness-driver-package.md
---

## Decided
Runtime THX admission resolves present software components to the exact codec ancestry and one installed service/APO package origin, verifies the actual API files against their bound Windows-trusted catalogs, and uses observed content only to invalidate cached state. Every modern V4 identity first reads both endpoints; each mutation separately repeats admission and a selected-endpoint typed read before the setter and readback afterward. Exact installation transaction pins remain separate.

## Tried and ruled out
A read-only RZ09-0581 probe exposed two false restrictions: THX SWC hardware IDs are carried by SWD\DRIVERENUM instance IDs, and multiple exact-codec devnodes may share the same service/APO package origin. Match hardware IDs, not only the instance prefix; reject multiple package origins. Do not require a DriverStore sidecar CAT for unrelated Windows inbox codec binaries that OpenBlade does not load. Do not demand catalog membership for PNF or other generated directory contents.

## Watch out
The local protected THX service denies user-token process-image query. Only ERROR_ACCESS_DENIED may use SCM configured binary plus a stable nonzero running own-process PID; the configured file still needs membership in the actual bound service CAT, a service recheck and typed API reads. Other query failures reject admission. This cannot independently observe the protected process image. Legacy COM's established registrations and PID checks remain. Native binding probes performed no vendor load or audio mutation and do not prove future-build native behavior or clean installation.
