---
title: Native power fence admits battery-preset class changes (corrects the HyperBoost exit learning)
author: Jack
date: 2026-11-25
branch: feat/native-hyperboost-battery-exit
---

## Decided
- This corrects the 2026-11-25 HyperBoost exit learning: native no longer stops a battery-preset recipe on an unplug or plug-in. The shared `PowerFence::admits` dropped `current != 1 && source != 1`; a class change continues when `admits_class` accepts the new reading and `destination_holds` is true. It still stops on a refused class (unclassified for a battery preset, battery for an AC preset, a Windows disagreement), on any change while pinned, and when the destination no longer holds (a retired temporary override, a profile that differs).
- A performance setter's Windows presence is released once its SET is sent and again before the next setter's reading (`PowerFence::release` in `submit_setter`), matching managed `OrdinarySourceUnchanged`, which checks only up to the SET. Fan operations do not call `release` and keep presence to their end.
- The restore pair's intents (80, 82) are attributed to Balanced for drift (`RequestPermit::record`), so a stop at 81 or 83 does not report OpenBlade's own Balanced as an external change.
- C# retirement binds epochs with performance rows 80-83 to the `service-thermal-hyperboost-exit-v1` capability marker, like the fan, battery and temporary families.

## Tried and ruled out
- Releasing presence after the acknowledgement instead of after the send: a Windows change while the SET is in flight then stopped the recipe before its reply, leaving a pending row where managed completes.
- A drift test baselined with `rig.get()`: status reads do not feed the tracker; only `observeState` publishes readings to it. A HyperBoost reading reports no preset, so the test needs a Performance observation before firmware enters HyperBoost, or the misattribution is invisible.

## Watch out
- `NativeArtifact.Inspect` refuses the exit marker for packaging, so the producer artifact in the C# test is built with `with { Capabilities = ... }`. In a packaged install every 80-83 epoch is denied until packaging admits the exit.
- The `is_intent` effect at the transition admission (host thermal_mutation.rs, `t.admits(false)`) is unreachable for 80/82: `Transition::decide` excludes a HyperBoost baseline, so a reconciliation never plans the restore pair.
- Pace budget mutants need a non-zero append cost under paused time: set `MemoryFiles::paced_replace` for the journal. At 450 ms per append the restore recipe completes, at 600 ms it is refused, and a direct battery pair still fits at 600 ms.
- The fixture now has `change_windows_after_write` (applied when that 0D setter is received) for changes after a SET.
