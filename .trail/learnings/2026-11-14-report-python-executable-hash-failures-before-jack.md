---
title: Report Python executable hash failures before fixture setup
author: Jack
date: 2026-11-14
branch: feat/native-fixture-ci
touches:
  - build/NativeWindowsFixtures.ps1
  - tests/Build.Tests/NativeWindowsFixtures.Tests.ps1
---

## Decided
Treat an unreadable or unhashable resolved Python executable as an explicit prerequisite failure. Get-FileHash uses ErrorAction Stop inside a guarded block; the diagnostic names the hashing step and executable while retaining the underlying exception and detail. It stops before fixture roots, SID setup or builds. Keep the actual supported resolver path and hash behavior unchanged.

## Tried and ruled out
A supported Store/AppExecLink incompatibility was not established, so this diagnostic does not introduce another resolver or interpreter fallback. Missing automated receipt write-mirroring was a coverage observation, not a current runtime/evidence defect: the existing positive PowerShell and driver receipts were independently joined. No additional receipt contract is required for this fix.

## Watch out
Verify the injected executable-hash failure and actual supported resolver in both Windows PowerShell5.1 and PowerShell7. Reuse the sealed493c native/managed run only through explicit unchanged relevant-source bindings; do not call it a new-head E2E result. Native, managed and Python driver code is unchanged, so replaying the nineteen/seven matrix solely for this diagnostic adds no new evidence about the failure path.
