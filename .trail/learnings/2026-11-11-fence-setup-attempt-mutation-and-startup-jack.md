---
title: Fence Setup attempt mutation and startup marker publication accurately
author: Jack
date: 2026-11-11
branch: feat/native-metadata-recovery
touches:
  - src/OpenBlade.Setup/SetupCommandRunner.cs src/OpenBlade.Setup/SetupNativePackageTransaction.cs
---

## Decided
A preexisting Retired predecessor is not this attempt's mutation. Observe predecessor transition in Setup's catch only after mutation started, preserving original prerequisite/capture/cancellation failure and staging cleanup beforehand. After provisional validation, set possible native admission before entering startup marker publication: rename can succeed before a later publication/readback exception. Keep marker readback before explicit StartService and keep earlier configuration failures eligible for captured managed restoration.

## Tried and ruled out
There is no second epoch before Activate. Eager native entry already requires original privileged live Setup, exact package/current/frontier, custody and submission before launch publication/dispatch. Do not move the marker ahead of SCM configuration or change the correct Retired-frontier reason classification.

## Watch out
Possible retirement or admitted incoming startup still requires explicit recovery without replay or guessed rollback. Production-seam injected/private-filesystem regressions cover unchanged inventory before mutation, post-rename/readback failure, retained evidence and zero explicit startup dispatch.
