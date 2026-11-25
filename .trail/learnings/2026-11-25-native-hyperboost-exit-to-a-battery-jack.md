---
title: Native HyperBoost exit to a battery preset (service-thermal-hyperboost-exit-v1)
author: Jack
date: 2026-11-25
branch: feat/native-hyperboost-battery-exit
touches:
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/hyperboost_exit.rs
---

## Decided

- Native leaves HyperBoost for Battery Saver or Battery Balanced behind `service-thermal-hyperboost-exit-v1` (implies `service-thermal-mutation-v1`, enables `openblade-domain/hyperboost-exit`). The recipe is managed `ExecuteThermalSettersAsync` with `hyperBoostExitActions` (`ProductionBatteryPort.cs:2277-2528`): the restore pair (Balanced selector bytes), an independent 0D/82 read that must be Balanced with Automatic fans (`thermal_mutation::restored`), then the battery pair and the final read. That is 12 exchanges and four setters, journaled 0, 80-83, 1-4, 5. Packaging refuses the feature and its marker.
- Native keeps its per-setter 07/8C reading and `PowerFence` admission on every setter. Managed admits no command after the exit classification (`ordinaryControl && !exitedHyperBoost` at 2450), so native is stricter. That gap is documented and is not a bug to fix.
- Managed handles a firmware pre-exit by deciding the plan from the port's own fresh read. Native decides from the reading the request resolved on. Once firmware has left HyperBoost the request is the direct battery pair.
- Storage reserves `Progress::longest_chain`, so under the feature a direct battery pair reserves the 10-row restore chain and leaves 4 rows unspent. The C# reader always admits the restore chain for destinations 3 and 4 (no feature switch). The handback classifies 80-83 as pending only for those presets.

## Tried and ruled out

- Replaying managed's failure replies as text parity. With the runtime fake, managed replies Failed "Performance write is indeterminate: <stage>". It then reconciles from a fresh readback and leaves nothing pending (`writesBlocked` false, no unconfirmed). Native replies Blocked PENDING and fences the store. That is the existing uncertain-submission deviation, so the replay asserts it rather than equality.
- Expecting a temporary override to finish the exit across a full-to-limited change. The source change retires it, so the row stops at 83, as for a shortcut.

## Watch out

- The managed recorder's runtime fake cannot tell the two restore setters apart, because the port raises one message per stage. The vectors carry one row per stage per readback (prior HyperBoost or Balanced), and the Rust replay maps each row to the native cut of that stage.
- In the fixture, `final_read_timeout`, `final_read_malformed` and `reads_after` with n == 2 hit the intermediate read of the restore recipe, not the final read.
- Under heavy shared-target load one unrelated `tfc_corrections` test failed once in the exit+shortcut+triggers lane. It passed three isolated reruns and a full rerun.
- `dotnet test` of the Service or Deployment tests needs the Rust toolchain on PATH, because OpenBlade.Core.Native builds the ffi crate. `--no-build` reads the stale bin copy of a regenerated vector file.
- Every reachable battery or Windows change at a restore setter is also stopped by a later checkpoint's fresh authorization (the exact layer was not pinned), so a mutant that skips `fence.admits` for restore setters survives those cases. Only an unclassified reading under Windows AC isolates `admits`: a battery preset refuses unclassified power. `an_unclassified_reading_at_any_setter_stops_the_exit_without_replay` pins it.
- The thermal fixture runs in paused time, so latency tests are deterministic. At 250 ms both the true and a short pace count refuse the restore recipe. At 200 ms only the full count does, which is why the pace test uses 200 ms.
