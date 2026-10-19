---
title: Legacy Realtek packages already carry OEM codec tuning
author: Jack
date: 2026-10-19
branch: feat/thx-driver-provisioning
touches:
  - docs/thx-audio.md
  - docs/thx-installation-investigation.md
  - tests/OpenBlade.Tray.Tests/Fixtures/ThxLegacyStackCatalog.json
---

## Decided

The exact Microsoft Update Realtek packages for RZ09-0482, RZ09-0483 and RZ09-0484 contain OEM codec tuning in their signed RTAIODAT.DAT. The MDTR table maps subsystem 1A582020 to Razer_2020, 1A583006 to Razer_3006 and 1A583007 to Razer_3007. The 0482 data hash is E84591EF1106343CE3200795CDFADDC4D6347F6F86E8E28012CEC82F130E5A32; the identical 0483/0484 data hash is 98509C3F66CA9D20B12DE14A8063C25502C439840193D501E80A857EA510F08B. All passed signed-catalog membership verification. Record these as Realtek codec evidence while keeping legacy provisioning blocked.

## Tried and ruled out

This corrects the earlier claim that those base packages establish codec identity only or that all OEM tuning must live in a missing separate package. The profiles expose Realtek EQ, DRC, amplifier and speaker-protection blocks. Neither that evidence nor the INF-wide THX MFX GUID proves THX acoustic deviceEQ or how the driver enumerates generic VEN_THX software components. The SCU's AddSoftware runs after enumeration and its generic-config-fallback-V2 is not an exact Razer configuration. The missing item is a reviewed integration/installation path, not a proven missing standalone package.

## Watch out

The MDTR hardware records are 28 bytes from byte24; the fourth field is the profile index. Profile records are 28 bytes at 24+hardwareCount*28+4, with a 16-byte name. The shared field0x250B has undocumented semantics: do not name it a THX selector. Keep exact-model scope, component hashes and installableCandidate=false. A data profile and passing signature check do not establish successful installation or native behavior.
