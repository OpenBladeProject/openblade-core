---
title: HyperBoost exit fan-owner guard sits before select
author: Jack
date: 2026-11-25
branch: feat/native-owned-hyperboost
---

## Decided
- The HyperBoost exit checks `storage.outstanding_fan()` at its own boundary in `resolve_hyperboost`, before `select`: `select` returns the saved-intent causes (Gpu, NoEligiblePolicy) first, and the exit turns those into a restore-only exit, so relying on its cause order wrote performance rows behind an open fan owner.
- `Storage::record` refuses a Prepared performance row while a fan owner is outstanding, so the read-side thermal_history rule is also enforced at write time.
- The restore-only exit has no status projection of its own: publication re-projects the desired state from the saved policy (`ReadSource::current_policy`), and only a Reader writes.
## Watch out
- A shared selection helper's cause order is not an admission contract: put a gate that must hold for one caller at that caller's boundary.
- Journal settings records read integers through `Json::web_integer`, so a nested number in a string is accepted, as managed accepts it; enums bind numbers only. The managed vectors pin both.
