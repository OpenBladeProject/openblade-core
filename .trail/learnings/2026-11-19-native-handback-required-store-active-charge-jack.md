---
title: "Native handback required store, active-charge claim and fixture leak"
author: Jack
date: 2026-11-19
branch: feat/native-managed-journal-handback
---

## Decided
- ServiceControlRuntime's full constructor requires a non-null NativeHandbackStore. The Service composition passes its registered store; the convenience constructors and tests without a handback pass NativeHandbackStore.Unbound (no binding, Load reports none, nothing resolves). Passing null now fails the build under nullable warnings-as-errors, which is what pins the composition argument; no composition test constructs the runtime and its hardware clients.
- The battery handback claim carries ActiveChargeOnly, set when every claimed item is an active charge: the live slot, or a charge an earlier start confirmed ("continuing"). A pending cancel beside the live charge is not, so a mismatch there stays "mismatched" with the Battery latch.
- The handback known-answer vector has two cases: fromJournal (classification) and fromRecord (ownedFan, a staged journal digest, and a battery policy whose protection and temporary flags differ, so a transposition is caught).

## Tried and ruled out
- Deleting a CreatePrivateFixture root in WindowsNativeEpochFiles.Dispose: production owners (selections, handoffs, packages) dispose the epoch files they are given while the test still uses the root, and NativeRecoveryTests fail.
- Deleting those roots from a ProcessExit handler: vstest terminates the testhost within seconds, so it removes only the first few roots and never the large ones.

## Watch out
- The %TEMP%\OpenBlade.NativeEpochFixture-* leak comes from base-branch tests that call CreatePrivateFixture without deleting the root (NativeRecoveryTests alone leaves about 80 per run). The handback tests on this branch leave none: NativeHandbackTests uses its own temp root and the Service handback fixtures are deleted per test. A real fix belongs in those tests, deleting the root after the test's last use.
