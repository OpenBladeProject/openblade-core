---
title: Validate current Windows catalog policy and decoder packaging
author: Jack
date: 2026-10-25
branch: feat/snaptap-driver-install
touches:
  - AGENTS.md
  - Directory.Packages.props
  - docs/razer-brightness-driver-package.md
  - docs/snap-tap-driver-review.md
  - docs/snap-tap-vendor-artifacts.md
  - docs/snap-tap.md
  - src/OpenBlade.Platform/WindowsDriverApiTrust.cs
  - src/OpenBlade.Tray.Core/OpenBlade.Tray.Core.csproj
  - src/OpenBlade.Tray.Core/RazerDriverPackageTrust.cs
  - src/OpenBlade.Tray.Core/RazerKeyboardDriverPackage.cs
---

## Decided

Check current-OS driver catalog policy in addition to generic signature/member checks. The Razer 0255 .71 legacy catalogs use OSAttr 2:6.1,2:6.3 and Windows rejects them on current OS. Use the separately reviewed complete Microsoft Update Catalog 0255 .66 package with signed U INFs, its own common/filter pair and OSAttr 2:10.0. The static ABI review traces callback restoration, all six typed A/D IOCTL geometries, per-device hooks, forwarding and file-scoped cleanup. Native acquisition and both installer/runtime catalog verification passed for all five newly added recipes.

## Tried and ruled out

Do not bypass OS policy, edit signed INFs, mix older Razer-carrier U INFs with different catalogs or inherit a different model's common stack. Generic /kp verification does not establish current-OS admission. SHA1 catalog member keys are distinct from SHA256 artifact/content identity; runtime verification tries SHA256 first and retries SHA1 only for exact TRUST_E_NOSIGNATURE, after catalog trust/publisher validation. Other failures and cancellations never downgrade. SharpCompress's unsigned DLL fails the existing Store payload policy: compile the pinned seven-file public-domain LZMA SDK source into OpenBlade instead, retaining its notice and bounded wrapper. Do not relax signed-payload validation.

## Watch out

This supersedes the prior proposed 0255 legacy/co-installer recipe and the temporary unsigned decoder dependency. The .66 notification ring has 256 slots versus .76's 1024; both drop oldest on overflow. Closing a subscriber does not prove owned hooks are cleared; preserve explicit owned-hook restoration and uncertain-output recovery. Physical installation, reboot, input and recovery remain unvalidated. Windows SIP verification rejected a valid carrier through an async read/write download handle: close/flush the writer, re-hash under a read-only lease, and use an independent synchronous trust handle. CAB extraction is data-only and exact-inventory; no vendor executable, driver install, native vendor load or hardware mutation belongs in offline validation.
