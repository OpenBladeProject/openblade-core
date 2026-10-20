---
title: separate recovered legacy THX tuning from the missing endpoint binding
author: Jack
date: 2026-10-19
branch: feat/thx-driver-provisioning
touches:
  - AGENTS.md
  - CHANGELOG.md
  - docs/thx-audio.md
  - docs/thx-installation-investigation.md
  - docs/thx-legacy-interface.md
  - src/OpenBlade.SessionAgent/ThxAudioAdmission.cs
  - src/OpenBlade.SessionAgent/ThxAudioCompatibility.cs
  - src/OpenBlade.SessionAgent/ThxAudioHelperCommand.cs
  - src/OpenBlade.SessionAgent/ThxAudioProvisioningJournal.cs
  - src/OpenBlade.SessionAgent/ThxAudioProvisioningPackage.cs
---

## Decided

Legacy THX 2.6.5 SCU already embeds the supported2023 Razer models' acoustic tuning. The signed spatial-config-util4.5.5.0 executable maps subsystem suffixes2020->PI490,3006->SO690,3007->KI890. Its default devices-V2.yaml selects PI490 profile-20230502,SO690 profile-20221107,KI890 profile-20221122-PVT-red. Independently decoded embedded SpeakerPath JSON hashes are recorded in docs/thx-installation-investigation.md and catalog metadata. Missing THX acoustic tuning is no longer the fresh-install blocker.

## Tried and ruled out

The adjacent generic-config-fallback-V2 JSON is not the normal installed tuning source: its CopyFiles line is commented and main rejects generic fallback. Normal SCU uses Realtek subsystem identity and writes a merged ProgramData/THX/thx_spatial.conf.json; ISST_SSP adds -oem and selects differently. Realtek RTAIODAT contains separate exact Razer codec tuning. Ordinary ks.inf/wdmaudio.inf Include/Needs exist. E82A5EE8 SSTPPCfg entries are in AMD HDXACPRazer.inf only, absent the reviewed Intel HDXSSTRazer.inf, and are not the reviewed THX2.6 host/offload CLSIDs. The earliest visible exact Intel6.0.9452.1 package also lacks the THX binding rule. The modern absent-device fallback merely stages an INF; it does not establish legacy SWC creation.

## Watch out

Fresh legacy provisioning stays Blocked/CandidateOnly/installableCandidate=false. The missing datum is an original signed integration rule/installer or factory SetupAPI+original signed INFs+actual parent-child nodes establishing THX SWC creation, endpoint FX/APO binding, normal versus ISST SCU branch, and order/rescan/restart. Do not invent SWC nodes, modify signed INFs, infer endpoint registrations, or claim generated final configuration/runtime/audio success from static embedded tuning. Earlier blanket no-Include/Needs, both-INFs-E82 and missing-tuning claims were overbroad and are superseded by these exact-source findings.
