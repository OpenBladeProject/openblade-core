---
title: Keep native temporary charge cancellation owned across source epochs
author: Jack
date: 2026-11-14
branch: feat/windows-native-temporary-charge
touches:
  - rust/crates/openblade-domain/src/lib.rs
  - rust/crates/openblade-domain/src/thermal_history.rs
  - rust/crates/openblade-domain/src/thermal_mutation.rs
  - rust/crates/openblade-protocol/src/battery.rs
  - src/OpenBlade.Deployment/WindowsNativePackage.cs
  - src/OpenBlade.Deployment/WindowsNativeThermalHistory.cs
  - tests/OpenBlade.Deployment.Tests/NativeMetadataRecoveryTests.cs
  - tests/OpenBlade.Deployment.Tests/NativeThermalHistoryTests.cs
  - tests/OpenBlade.Deployment.Tests/OpenBlade.Deployment.Tests.csproj
  - tests/OpenBlade.Deployment.Tests/WindowsNativeEpochTests.cs
---

## Decided
- Native exact0581 temporary charge has its own service-temporary-charge-v1 capability and history family. Keep basic battery-v1 bytes, meanings and14-ID recipe unchanged. TFC reserves8 operation IDs and8 one-shot source-cancel IDs before enable reads; use one full-byte guarded SET and a separate flags GET, never ACK as completion.
- Carry the actual creator request/PID/SID/session, saved logon LUID and original service custody/generation. A Battery event obtains fresh ordinary authority at the new lifecycle epoch; never reuse the stale enable guard or fan-only stop authority. Register prospective ownership before worker submission and adopt only after actual worker retirement.
- Desired-memory clearing is not cancellation ownership. Retire the obligation only after newer independent false evidence or its one-shot source claim. Unknown metadata leaves the epoch pending for bounded retry on the existing service tick; subscribers and GetStatus are not the event owner.
- Every shared writer, including performance, preserves cancellation capacity and refuses rollover while that live process obligation needs its same-segment origin. Retain the exact original fan creator and every handback ID; TFC terminals72/73 do not settle fans or hardware drain.
- Authenticate the original producer's TFC capability for every new-family row, including cached proofs. Both Rust and C# readers agree on canonical bytes, strict optional shapes, source-origin references and legacy interleavings.
- The shared presentation boundary withholds unrepresentable private Off+TFC intent through the existing Battery unavailable projection. Keep internal configured policy and temporary intent plus independently aged hardware evidence; no guessed firmware false or prerequisite policy write.

## Tried and ruled out
- A fresh Battery sample is not a reason to discard the owner merely because desired_battery cleared private memory; that strands the actual source cancellation.
- Consuming an event epoch on Unknown source loses same-epoch recovery. Reusing the old WindowsGuard always fails after the existing power-event fence advances.
- Reserving cancellation space only in battery/fan paths leaves performance able to spend it or roll away the origin. Scope the fix at every actual shared reservation site.
- Unconditionally including the TFC module breaks non-mutation read-only feature compositions. Shared Agent Cargo features do not grant Service capabilities.
- A profile edit test must update the real aggregate commitment; an uncommitted aggregate or missing private fixture lock is a harness prerequisite failure, not a reproduced runtime assertion.

## Watch out
- Current managed UI/service cancellation gates remain separate limitations; the native flags-only cancel admits Off/unknown policy under its own full safety checks. Installed defaults, C#/WinUI and physical qualification are unchanged by this offline Source stage.
- Keep the complete flags guard:07/0F replaces the byte. Refuse non-TFC or unknown bits; do not merge/reset other features. Unknown/failed/ambiguous submission pauses read-only, with no replay or alternative sequence.
- Temporary=true is process-only, never restart policy. Actual provider/installed/physical validation, finite production-ID lifetime, remaining Service/Agent/helper/model/CI and cutover work remain separate gates. Do not infer a permanent fan fix from the earlier unexplained injected failure and exact recheck.
