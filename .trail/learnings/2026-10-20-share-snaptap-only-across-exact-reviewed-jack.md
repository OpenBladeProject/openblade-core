---
title: Share SnapTap only across exact reviewed Blade driver profiles
author: Jack
date: 2026-10-20
branch: feat/snaptap-model-drivers
touches:
  - AGENTS.md
  - Directory.Build.props
  - docs/experimental-blade-support.md
  - docs/rz09-0528-support-migration.md
  - docs/snap-tap.md
  - src/OpenBlade.SessionAgent/RazerSnapTapDriverAdmission.cs
  - src/OpenBlade.SessionAgent/SnapTapDriverLoop.cs
  - src/OpenBlade.SessionAgent/SnapTapDriverSupervisor.cs
  - src/OpenBlade.SessionAgent/SnapTapProcessCoordinator.cs
  - src/OpenBlade.SessionAgent/SnapTapStatusPublisher.cs
---

## Decided
Razer documents SnapTap for Synapse 4-compatible Blades. Static review of exact 02C6, 029F, 029D, 02C5, 0255 and 02A0 filters establishes the shared version-1 keyboard callback interface consumed by pinned RzCommon 1.0.0.76. Share the existing A/D codec, transport, supervisor and recovery logic; keep exact host, PID, filter hash and same-container topology in profiles. This intentionally supersedes the earlier lack of a Blade 18 SnapTap ABI review. New profiles are source-reviewed experimental; 02E0 retains its recorded physical validation.

## Tried and ruled out
RzCommon equality alone and Razer support marketing alone do not prove filter compatibility. The older .71 and .73 binaries were reviewed independently: callback registration, original-output bypass, device association and cleanup agree. No Windows hook fallback, vendor mapping engine loading, new kernel driver or firmware commands were added. The 0.38.0 minor version is intentional capability expansion; lexical trail conflicts with older version snapshots do not invalidate that bump.

## Watch out
Automatic installation remains 02E0-only. Missing drivers on other models must never expose that installer. The 0255 package catalogs cover legacy INF files, not the parallel U INF variants; use Synapse-managed setup. Physical tests were not run for the new profiles. Keep fresh admission before driver access and existing stop-on-ambiguity ownership recovery.
