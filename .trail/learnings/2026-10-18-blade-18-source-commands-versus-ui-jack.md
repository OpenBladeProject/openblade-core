---
title: Blade 18 source commands versus UI policy and observed full-charge evidence
author: Jack
date: 2026-10-18
branch: feat/blade18-observer-support
touches:
  - CHANGELOG.md
  - docs/blade18-observer-evidence.md
  - docs/blade18-source-feature-recovery.md
  - docs/experimental-blade-reference.md
  - docs/experimental-blade-support.md
  - docs/ru-RU/experimental-blade-support.md
  - docs/zh-CN/experimental-blade-support.md
  - src/OpenBlade.Protocol/ExperimentalBladeFactory.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.Fans.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.cs
---

## Decided
For Blade 18, a missing Synapse UI activation flag is not a firmware prohibition when the exact inherited command contract is defined. Startup animation uses exact Product672 GET 0F/98 [0] and SET 0F/18 [0,disable], disable0 meansenabled. Admit the control after a valid getter, keep first-read failures independent of other controls, require fresh baseline and post-SET GET, accept successful non-echo ACK, pause after ambiguity. Existing per-zone fan target setters/getters support independent target-register requests within admitted ranges; physical RPM independence remains unverified.

## Tried and ruled out
The October17 ZIP did not capture temporary full charge: three 07/8F [00] queries and three 07/0F [00] writes, no 04 enable; full-charge coverage is NotInvestigated and returned bodies are withheld. RawInput plus a low-level keyboard hook cannot provide reliable system-wide per-device suppression. The preferred route is a proved public ABI for the exact installed signed Razer filter, not guessing from internal callbacks or adding a kernel driver.

## Watch out
Synapse source forces Automatic on Custom entry as host policy, not demonstrated firmware rejection. Match the clear policy through a service-planned Automatic handback before Custom, preserving saved manual intent and accounting for both affected subsystems. Never nest fan setters under the protocol operation gate. Observer PR21 separately adds fixed-alias read-only PDH evidence alongside WMI; zero WMI rows alone do not establish PDH availability. Trail check keyword matches across earlier branch work do not overturn these precise source and readback contracts.
