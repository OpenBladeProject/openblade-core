---
title: Recover legacy THX child enumeration separately from endpoint FX binding
author: Codex
date: 2026-10-19
branch: feat/thx-driver-provisioning
touches:
  - docs/thx-installation-investigation.md
  - docs/thx-legacy-interface.md
---

## Decided

The factory-driver mirror at Torahime3/razer-blade-14-thx-driver-restore commit4f3451e246a93d48dabf8e2140ce3aebbd2a08f4 contains THXHDAudio2.6.5.0 with exact2023 codec IDs2020/3006/3007. Its SKU_C/SKU_A sections declare THXAPO,THXSVC,THXHSA,THXSCU normal children, model endpoint types and ProgramDataTHXconfigpath. This resolves signed child-enumeration declarations and the normal subsystem-based SCU branch for0482/0483/0484. Preserve full exact-device admission and keep fresh provisioning blocked until matching endpointFX and installation contracts are established.

## Tried and ruled out

The Git-exported INF is LF normalized and fails catalog membership. Restoring CRLF without directive changes reproduces the original signed SHA2566FE831463C60FD2C1ADA9207FD63607491BEA87F70AE5D48B9982F32B6571D72,29311bytes. OriginalTHXHDAudio.catSHA256FB496566667F6AE7C415A96BFE3D97B14BD63C1681C19B9906A21EBF0AFFE799 verifies under kernel policy and authenticates the restoredINF. The signed2022RealtekFXextension hdx_razerext_thx_rtk_gen4_kd.inf6.0.9292.1 omits all3targetIDs. Even exact-model Realtek6.0.9909.1 CatalogCAB supplies onlyMEDIAcodeccontent. Catalog searches also miss the knownsigned2022extension, so zero public results do not prove nonexistence.

## Watch out

The THXINF's explicit CompositeFX CLSID writes belong to different AE34 IntelSSP IDs, not these3 codec matches. Do not copy them, extend the2022model list, or claim complete Synapse-free installation. The publicmirror is a source of authenticated vendor artifacts, not a verified2023recovery image or an admitted distribution route. Official recovery requires an owner's RazerID/registeredserial and transient48hourlink; no exact2023image was acquired. Inspect archives and selectedWIMfiles without applying/booting recovery; archive integrity is not a driver signature. Current sourcepins and remaining limits are in docs/thx-installation-investigation.md.
