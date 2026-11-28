---
title: Native power-only block release and null laptop control sets
author: Jack
date: 2026-11-24
branch: feat/native-power-only-block
---

## Decided
- Native has no latched power-only write block. Every step reads 07/8C fresh; a battery class under Windows AC (`battery_reading_contradicts_windows`) or an unclassified one without AC presence refuses only that step, and the owed cause applies at the next lifecycle epoch. Porting managed 3f7b3838/1a03e358 therefore meant porting only the published source.
- `GuardedStatus::publish_power_source` observes Windows metadata once per lifecycle epoch on the origin tick, before `reconcile_lifecycle`. Without it, the destination's publication rebased onto the source from the last 2 s refresh (aps 3 after an unplug). It mailbox-decorates only when `ObservedStatus::publish` reports changed metadata. Re-decorating unchanged metadata drops the TFC "Battery operation confirmed" message (`tfc_release_outcome_explicit_cancel_keeps_settled_result_and_immediate_owned_delta`).
- 05ec5381: `Snapshot::verify` refuses a present, non-array `oneShotLaptopControls` or `hostManagedLaptopControls`. An absent set stays accepted. The rows in `service-object-frames.json` with `nativeError` pin this through both codecs.
- e4f19946/b3b2b5ae do not apply natively: they change only the experimental 0x02A0/0x02C5/0x02C7 battery backends, and the ExternalUnknown paths there; native battery is exact 0581 and needs classified power.

## Tried and ruled out
- Starting the failure/recovery test from a rig whose activation was already refused: the debt's retry budget is spent by then, so the next notice applies nothing. Start from an unblocked rig and `age(pace())` before the retry notice.
- `fail_write` for the "unresolved destination" case: it leaves observation unavailable afterwards. Use `mismatch`, then `abandon` the rig, because Stop reports an unresolved fan submission.

## Watch out
- Git Bash `sed -i` rewrites a CRLF doc as all-LF. Count `\r` bytes with `tr -cd '\r' | wc -c` (grep -c $'\r$' is unreliable here) and fix with `unix2dos`.
- `admits_class`'s Windows check for class 1 is a layered gate. The host suite cannot kill a mutant there because `battery_reading_contradicts_windows` and `PowerFence::authorize` catch it first; only the domain unit test does.
- `publications()` is None until the first publish, so a readiness test must `rig.get()` before reading the feed.
