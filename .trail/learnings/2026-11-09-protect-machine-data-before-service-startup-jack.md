---
title: Protect machine data before service startup
author: Jack
date: 2026-11-09
branch: fix/issue-911-device-discovery
touches:
  - src/OpenBlade.Setup/WindowsSetupPlatform.cs
  - src/OpenBlade.Setup/MachineDataDirectorySecurity.cs
  - tests/OpenBlade.Setup.Tests/WindowsSetupPlatformServiceTests.cs
---

## Decided
Protect the exact shared machine-data root before fresh or upgraded setup starts the service. Multi-user setup stopped calling the legacy SID writer, which had also been the only call to protect this root. The issue #911 export still identified RZ09-0581/02E0; the unsafe root blocked cooling-pad state access and left Fan Stop startup fencing the ordinary device probe.

## Tried and ruled out
Do not restore the obsolete SID write, relax either service ACL check, or bypass Fan Stop recovery. Do not recursively re-own existing descendants in the current install flow: doing so could grant trust to untrusted recovery records.

## Watch out
Use isolated test paths or a fake ACL backend and guardian guard. Keep pending guardian evidence intact and recheck quiescence before starting the service. The Trail findings on the version, Fan Stop, diagnostics, and unrelated UI and IPC work were lexical matches, not semantic reversals.
