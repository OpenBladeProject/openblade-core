---
title: HyperBoost battery handoff scope and DC Balanced Custom entry (PR 993 round 4)
author: Jack
date: 2026-11-21
branch: feat/rz09-0510-hyperboost
---

## Decided
- A mode-7 HyperBoost journal retires as a handoff only on battery, for a battery destination (Battery Saver or Battery Balanced) that the fresh table does not list, with no deferred retry target, after a fresh read shows HyperBoost off with Automatic fans and no pending write confirmation. `RestoreUsesCurrentPowerDestination` is allowed on battery because the real battery-boundary exit always sets it (the source changed while owned); on battery the current destination is the battery profile. Everything else keeps the earlier restore path: AC recovery of a battery-mode journal applies the plugged-in destination, a deferred current-destination retry writes it, and an AC intent narrowed by an observed fan range stays pending.
- Synapse enters Custom straight from the current mode (671 mw-6737 bytes 1,398,696/1,398,993; 669 1,558,801/1,559,098; 695 1,893,039/1,893,371). The protocol Custom setter therefore accepts a Battery Balanced (raw 6) baseline off battery on 029F, 029D and 02B7 only; fan baseline, Maximum Fans flags, stored levels and readback checks are unchanged.
- On 029F/029D, raw 6 read off battery (Battery Balanced, not in their tables) is not an outside change at AC return, handoff or not. The service has no product id, so this is keyed on the table shape: HyperBoost table listing Balanced but not Battery Balanced. The same predicate scopes the Balanced/Battery Balanced handoff equivalence; 02B7 lists Battery Balanced, so its raw 0 Balanced is a real change.
- The compensating exit after a failed journal finalization calls `BeginIntentRestoration()` first, and the submission/retry gates use `forwardEntry` (not `enabled`) for the pad, base access, direct-entry base, padless access and admitted-intent checks.

## Tried and ruled out
- Narrowing literally to "no RestoreUsesCurrentPowerDestination" breaks every real battery exit: Path B and Path C journals carry it.
- A Service test for the `!pendingWriteConfirmations.HasPending` guard alone: recovery already gates on it at entry, and observation reconciles pending confirmations from the same read before journal retirement, so only a subsystem whose readback stays Unavailable can keep one; a lighting attempt in the fake resolved immediately.

## Watch out
- With the K1 rule, an outside there-and-back to raw 6 on battery followed by AC return on 029F/029D applies the plugged-in profile; tests that need the handoff-clear to be observable use 02B7.
- Reviewer probes log only. A mutant surviving a probe filter proves nothing; write assertions.
- The deferred current-destination restore runs on the second observation (the first publishes the table).
