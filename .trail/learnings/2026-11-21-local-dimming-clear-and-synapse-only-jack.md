---
title: Local dimming clear and Synapse-only observed features
author: Jack
date: 2026-11-21
branch: feat/observed-cpu-power-dimming-gpu-mode
---

## Decided
- Local dimming Clear (07/8F bit 0x08) is admitted only on RZ09-0510 (02B7) and RZ09-0509 (02B8), and only after 0D/8F getSKUHardwareConfiguration reports Mini_LED_Resolution 0x02 at port open; it is re-read before every Clear. The write is the whole byte current & 0x04, refused while 0x01 or 0x02 is set; ambiguity pauses writes, no replay. Shared rules live in LocalDimmingProtocol (PR #994).
- RZ09-0530 and RZ09-0528 local dimming stays ObservedOnly: Synapse couples it to Windows HDR (709 policy ON_ENABLE_LOCAL_DIMMING with isHDRModeEnable). Series 10 is excluded from that coupling.
- Intel PL1/PL2/tau, AMD 45-95 W CPU power and firmware Gaming GPU overclock are ObservedOnly: Synapse writes them through native vendor DLLs (rule 6).
- ResumeStateValidationTracker has an UncomparedStatePropertyNames set; LocalDimmingActive is in it because firmware/HDR change it on their own. The allowlist test requires every ConfirmedDeviceState property to be in exactly one set.

## Tried and ruled out
- Fetching a fresh lease port for the dimming read in ReadBatteryStateOnceAsync unconditionally: it reopens a retired port after an isolated getter failure and breaks the scoped-failure tests. Gate the read on the factory capability (LocalDimming Access ReadOnly).
- There was no existing RZ09-0581 Clear path to reuse, despite the brief saying so.

## Watch out
- Protocol 85 is also claimed by open PRs #984 and #992 (and #993 takes 86); the later merge must renumber and regenerate the IpcWireShapeManifest golden.
- A copied native payload embeds productVersion; building tests with -p:OpenBladeVersion different from it makes about 90 Protocol tests fail with NativeComponentException category 107. Build with only -p:OpenBladeNativeDevelopmentBuild=false.
- src/OpenBlade.Protocol/*.cs are eol=lf; new files there must be LF.
