---
title: Bind machine-data permissions to an admitted directory handle
author: Jack
date: 2026-11-09
branch: fix/issue-911-device-discovery
touches:
  - src/OpenBlade.Setup/MachineDataDirectorySecurity.cs
  - tests/OpenBlade.Setup.Tests/MachineDataDirectorySecurityTests.cs
---

## Decided
Create the machine-data directory with its privileged ACL from the first filesystem operation. Verify existing roots by opening the directory itself, not any reparse target, and hold that same handle across owner and attribute checks, ACL application, and readback.

## Tried and ruled out
A pathname check followed by a pathname ACL write is not a single identity check. Default Windows ProgramData lets standard users create and own a missing child; rejecting a junction after the ACL write is too late. Do not give ordinary install or upgrade a recursive ownership pass.

## Watch out
Root ownership must already be SYSTEM or Administrators on upgrades; refuse an unexpectedly owned or redirected root. Use a temporary NTFS junction and check its target descriptor is unchanged when testing the native boundary.
