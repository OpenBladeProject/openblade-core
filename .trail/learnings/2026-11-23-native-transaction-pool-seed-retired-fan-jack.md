---
title: "Native transaction pool seed: retired fan preflight, real demand and the N5 counterexample"
author: Jack
date: 2026-11-23
branch: fix/native-pool-seed-from-journal
---

Supersedes "Native transaction pool seeds each family's newest request after a restart" (2026-11-23). Three points in it are wrong. The N5 mutant is not equivalent. A retired fan request's preflight must be seeded. The demand behind the cap was 46 and is really 66. The rest of that learning still holds.

## Decided
- A retired (6) fan request adds `preflight` and `ids` to the history (`transaction_seed::known_unsent`, formerly `after_prepared`), but not `cleanup`.
  - The newest fan row can belong to an older record. Example: owner at 30, then a second request retired at 6, then the owner's settle-only 51, which sends no 07/8C (fan.rs).
  - The retired request's answered preflight can then hold the newest 07/8C reply.
  - The history is truncated by LIMIT, so this changes no bound.
- DEMAND is 66: a live fan owner's whole record (26, pinned until it settles, `FanPermit::settle_pins`), a temporary-charge owner's cancel (8), the next fan request with its Custom levels (26 + 2), and a read with its Custom levels (2 + 2).
- The assert is now `LIMIT >= LIVE && 254 - LIMIT >= 2 * DEMAND`, with the margin cut from three to two on purpose.
  - Three peaks (198) fit beside no limit that holds the live set (254 - 74 = 180).
  - LIMIT stays 80. Its 174 free IDs are two peaks plus about 42 more.
  - The test pins `(FAN, LIVE, DEMAND, LIMIT) == (26, 74, 66, 80)`.
- Keeping a fatal seed-read failure is intended to stop startup.
  - `join_row`'s owner-binding FatalAdmission is now kept in `ReadSource.fatal`, so the startup GetStatus probe fails and the Service never reaches RUNNING, including on a new package's provisional start.
  - Before the seed, such a Service started and failed at its first capture.

## Tried and ruled out
- Calling the dropped-superseded-from-`sent` mutant (N5) equivalent. "LIMIT >= LIVE leaves room" reserves room for the live set only.
  - Under the mutant the set is only in history, behind newer row-6 sets.
  - Counterexample, now a test: `source-cancel-fresh-ids-first`, then six battery requests at [0, 6] with ids 1..84. Only the live copy keeps 108..=115.

## Watch out
- The fixture `retiredThenObservedAutomatic` gives the retired request the owner's IDs, so it cannot show the preflight gap. The test uses `fan(100)` for the retired request.
- A "3x headroom" style assert must be derived from the real peak. Check what each reservation pins, and for how long, before sizing it.
