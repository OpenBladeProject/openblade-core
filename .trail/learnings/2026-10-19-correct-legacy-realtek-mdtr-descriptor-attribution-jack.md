---
title: correct legacy Realtek MDTR descriptor attribution
author: Jack
date: 2026-10-19
branch: feat/thx-driver-provisioning
touches:
  - AGENTS.md
  - docs/thx-audio.md
  - docs/thx-installation-investigation.md
  - tests/OpenBlade.Tray.Tests/Fixtures/README.md
  - tests/OpenBlade.Tray.Tests/Fixtures/ThxLegacyStackCatalog.json
---

## Decided
Parse signed Realtek MDTR tuning containers from their actual 16-byte header. The 28-byte hardware rows start at offset 16, select profile index at row +20, and lead to the descriptor table at 16 + hardwareCount * 28. Each 28-byte descriptor stores payload offset +0, record count +4, payload size +8, and ASCII profile name +12. Pair a name with its own descriptor. Target Razer_2020, Razer_3006 and Razer_3007 payloads contain one RTKAMPI record each and remain byte-identical across reviewed 6.0.9484.1, 6.0.9601.1 and 6.0.9909.1 files.

## Tried and ruled out
The earlier parser began hardware rows eight bytes late and descriptor rows twelve bytes late. It incorrectly assigned the next MSI_13D7, Acer_1654 and MSI_13D3 records to the Razer profiles. Those per-profile record interpretations are superseded; signed whole-DAT hashes, Razer profile-name mappings, and independently decoded THX SCU JSON pins are unaffected. A bounded static trace of catalog-covered Realtek 6.0.9909.1 confirms the corrected selector layout. DCH_THX feeds internal settings flags; inspected InterfaceSetting paths copy existing named registry sections, not a demonstrated THX CompositeFX synthesis.

## Watch out
Hardware row +24 contains 0x250B but the inspected selection loop does not consume it: do not label it an FX selector. Realtek RTKAMPI tuning is not proof of THX endpoint binding. The catalog remains CandidateOnly/Blocked/installableCandidate=false for the three legacy models until exact endpoint FX and installation/rescan/restart provenance are established. Do not edit an INF, invent child devices, or infer runtime success from static source. The corrected catalog profilePayload pins and regression mutations reject the previous neighboring-profile attribution.
