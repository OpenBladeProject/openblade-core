---
title: Native-to-managed thermal journal handback
author: Jack
date: 2026-11-19
branch: feat/native-managed-journal-handback
---

## Decided
- A crashed native thermal producer is handed to managed, not refused. Setup's RetireRecoverableSelection publishes attempts/exited-PID-CREATED.bin (OpenBlade.NativeAttemptExited.v1) only under the exact reacquired custody and only for thermal-mutation producers; every other reader accepts an existing exact marker and never publishes.
- WindowsNativeThermalHistory.Classify yields pending, ownedFan, liveTemporaryCharge and abandonedPrepared, each owned by the exited nonce. Setup writes ProgramData\OpenBlade\native-handback\EPOCH.json before retirement; without a writer, retirement refuses with FirmwareOwnerUnresolved.
- NativeRetirementProof.v4 = v3 + handback commitment, emitted only for the generation with a non-empty record. ManagedMailboxNativeCustody.ThermalMatches requires the same version, except the legacy v2-to-v3 empty-thermal case.
- The Service (NativeHandbackStore) never deletes records. It persists outcomes write-ahead in resolutions.json and reuses PendingWriteConfirmationTracker plus unexpected-exit recovery. Owned fans are reported only, with no fan write.

## Tried and ruled out
- Publishing the exit marker for every producer: a metadata-only crashed owner then retires, breaking NativeMetadataRecoveryTests.MetadataCapabilityDoesNotBypassIndependentOwnerOrMalformedHistory.
- Deleting or rewriting the record to mark it consumed: consumption lives in resolutions.json, so the record stays immutable evidence.

## Watch out
- Native Rust require_launch_budget refuses the unknown exited- attempt name, so the epoch cannot relaunch. That is fail-safe, but Rust does not accept the marker yet.
- A .rollover file still refuses retirement. Performance confirmation compares the preset only, not GPU offsets.
- Fixture roots under the long scratchpad path hit MAX_PATH ("native epoch path could not be retained"). Use a short root such as C:\tmp\...
- After a mutation run, rebuild before running the suite: stale mutated binaries looked like a flaky NativeThermalHistoryTests "pending" failure.
- Pre-existing failures at base 265594e5: Setup WindowsSetupServiceImagePinTests (5), Service WindowsReplay v79 (7) and ServiceCompositionTests.AddVerifiedBladeDeviceProvider_ResolvesWindowsProviderOnly.
