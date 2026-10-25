---
title: Install remaining SnapTap models from signed data-only carriers
author: Jack
date: 2026-10-25
branch: feat/snaptap-driver-install
touches:
  - AGENTS.md
  - CHANGELOG.md
  - Directory.Packages.props
  - docs/razer-brightness-driver-package.md
  - docs/snap-tap-driver-review.md
  - docs/snap-tap-vendor-artifacts.md
  - docs/snap-tap.md
  - src/OpenBlade.SessionAgent/RazerSnapTapDriverProfile.cs
  - src/OpenBlade.Tray.Core/OpenBlade.Tray.Core.csproj
  - src/OpenBlade.Tray.Core/RazerDriverPackageTrust.cs
---

## Decided

Extend explicit experimental SnapTap setup to all seven reviewed profiles. The five remaining models use exact official Razer NSIS carriers as data, plus only the generic signed RzCommon files and unchanged notice from the pinned catalog ZIP. Each filter creates the generic RAZER DeviceContol child and exposes the independently reviewed callback/query contract; this is separate applicability evidence, not inheritance of another model's complete stack. Fixed carrier hashes, successful Windows signer state, bounded LZMA blocks, output hashes, catalog membership and explicit role INF names precede native staging/binding. Build the recipe registry in an explicit static constructor because vendor entries live in another partial file.

## Tried and ruled out

Do not execute vendor installers, DPInst, Removeinf, NSIS scripts or plug-ins. Do not extract another model's filter from the common source ZIP. 0255 U INF siblings are not catalog members: use its signed legacy four-role INFs and include the catalog-covered WdfCoinstaller01009.dll closure unchanged. Modern Windows selects NTamd64.6.2 without registering that legacy co-installer. Runtime driver compatibility still uses the installed API contract rather than fixed build hashes.

## Watch out

This supersedes the earlier five-model manual-setup-only boundary after independent source and catalog review. It does not establish physical installation, reboot, input or recovery results on those laptops. Preserve exact host/topology and filter checks, global common-INF target exclusion, fresh admission, durable operation identity, readback and stop-on-ambiguity. No vendor binary is bundled or newly published. The added managed decoder and its license belong in the application payload.
