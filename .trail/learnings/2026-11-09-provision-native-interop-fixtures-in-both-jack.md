---
title: Provision native interop fixtures in both Windows CI modes
author: Jack
date: 2026-11-09
branch: fix/service-startup-timing
touches:
  - ".github/workflows/build.yml"
  - build/Run-AffectedCi.ps1
  - tests/Build.Tests/AffectedCiRunner.Tests.ps1
  - tests/Build.Tests/CiBuildScope.Tests.ps1
  - rust/Test-NativeInterop.ps1
  - OpenBlade.Native.slnx
---

## Decided
A Contracts edit reaches Core.Native.Tests through the real Contracts to Core.Native to test project references. Keep that affected selection. Run the suite through rust/Test-NativeInterop.ps1 in both Windows quality modes so its real Rust library and its policy-only and automation-only C fixtures are supplied.

## Tried and ruled out
Plain dotnet test of the selected native project built the production library but failed 84 of 133 tests in PR 904 because it did not supply the three explicit fixture variables. The standalone Rust matrix passed with its own setup. Removing the affected selection or relying on the product solution alone would lose coverage.

## Watch out
The native tests are in OpenBlade.Native.slnx. Windows affected CI uses Windows PowerShell for its runner and starts the existing interop script in PowerShell 7 because the script uses its platform variables. Changes to the CI workflow or affected runner force full validation, so exercise an affected native-only plan separately.
