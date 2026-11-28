---
title: Keep native Windows fixture provenance and CI inputs explicit
author: Jack
date: 2026-11-14
branch: feat/native-fixture-ci
touches:
  - ".github/workflows/build.yml"
  - build/CiImpactManifest.json
  - build/Run-AffectedCi.ps1
  - tests/Build.Tests/CiBuildScope.Tests.ps1
  - tools/openblade-windows-host/src/windows/scm/installed/battery_mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/temporary_charge_release.rs
  - build/NativeWindowsFixtureManifest.json
  - build/NativeWindowsFixtures.ps1
  - build/Test-NativeWindowsFixtures.ps1
---

## Decided
Run the Windows fixture producer from Cargo's exact service-candidate test artifact and an explicit per-tuple case manifest. Basic battery publication semantics use the battery-policy tuple; temporary-charge corrections use their own tuple. Preserve encoder bytes and distinguish peer responses, subscriptions, publisher projections and synthetic projections; TRACE copies are not deliveries. Fresh managed edge fixtures must match the committed include_bytes inputs.

## Tried and ruled out
The ordinary managed native dependency builder creates a per-output Cargo target at this accepted base. The fixture stage instead prepares its required FFI dependency separately in the shared cache, records source/compiler/binary/payload provenance and supplies supported MSBuild properties. It does not change the production builder or widen native interop test scope. Setting a protected directory's owner after creation failed on the development host; atomic protected creation works.

## Watch out
Saved-Custom subscription events need the statusEvent operation label for strict edge coverage. Historical frame counts calibrate parsers, not future required counts. Own-SID NTFS setup is a real identity/filesystem boundary, while injected peers remain synthetic. Full selector baseline Service-to-Tray selection is separate from the focused fixture routing checks. Selected harness results do not establish full-suite, hosted CI, merge or installed-product readiness.
