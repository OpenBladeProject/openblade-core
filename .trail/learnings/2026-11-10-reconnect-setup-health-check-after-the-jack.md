---
title: Reconnect Setup health check after the first startup pipe drops
author: Jack
date: 2026-11-10
branch: fix/display-brightness-no-wmi-status
touches:
  - CHANGELOG.md
  - src/OpenBlade.Setup/WindowsSetupPlatform.cs
  - tests/OpenBlade.Setup.Tests/SetupServiceHealthProbeTests.cs
  - tests/OpenBlade.Setup.Tests/WindowsSetupPlatformServiceTests.cs
---

## Decided
Protected Setup and service timing for PR 942 showed the new service accepted the connection about seven seconds into the ten-second Setup budget. Its two-second first-frame read expired before Setup reached the first header write around 12.2 seconds. Setup now uses a single 30-second deadline and one reconnect after an established read-only status connection drops.

## Tried and ruled out
Lengthening the Setup deadline alone leaves the already closed first pipe unusable. Preserve the server frame deadline, status validation, caller cancellation, and fail-fast handling of incompatible protocols. The rollback health probe logged a protocol mismatch against the prior build; that happened after the install failure and is separate from the trigger.

## Watch out
Only an established transport failure qualifies for reconnect. The two attempts share the same deadline. Keep broker submission distinct from the read-only Setup health retry, and never resubmit the signed installer sequence.
