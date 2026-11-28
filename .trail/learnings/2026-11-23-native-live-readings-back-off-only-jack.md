---
title: "Native live readings: back off only after a sent fan SET, bind the record to its producer"
author: Jack
date: 2026-11-23
branch: feat/native-live-readings
---

Supersedes "Native live readings review: capability record only beside HyperBoost, fan pair back-off" (2026-11-23) on when the fan pair backs off and how the observation record is bound; the rest of that learning still holds.

## Decided
- The 0D/88 back-off escalates only when a pair's first SET actually went out and no confirmed pair came back. The worker counts each pair just before it submits the first SET (`FanPairSends`, an Arc counter handed to the worker through `Queries.fan_rpm`). `live_readings::Host` compares that count with the count it last accounted for, so the job result no longer decides whether anything was sent.
- A job that sent no SET (failed thermal read, worker that did not start, reading discarded at the deadline, budget refusal before the first SET) leaves the pair due and the pause unchanged. A mode the thermal read cannot confirm no longer holds fan speeds null for 5 minutes.
- An observation dropped after the SET (a disconnecting client drops the future, and the worker is cancelled) never settles; the next `Host::fan_query` accounts for its pair as unconfirmed. The count lives on the worker side for exactly this reason.
- The observation capability record is bound to its single producer rather than a runtime identity. A confirmed state names no model, `ReadAdmission` names custody, and `with_mailbox_read` must not gain a parameter. `service_status::OBSERVED_CAPABILITIES_MODEL` names the record's model, `performance::OBSERVED_IDENTITY` names the Reader's, `mailbox_read` debug-asserts they match, and `capability_tests` pins both, the battery evidence's device, and the exact set of non-test files that call `.with_mailbox_read(` (fan_mutation, mailbox_read, thermal_mutation).
- The mutation drop is tested through the production `ReadSource::mutate` and `mutate_cleanup` with a refused fan permit (`Rig::with_mutation`, built on `injected_mutation`). Each entry drops the retained pair after taking the serial lock and before any admission, so a refused mutation also drops it.

## Tried and ruled out
- Recording "sent" from the job's result (the previous version). It cannot tell a failed thermal read from a failed pair, and it records nothing when the future is dropped.

## Watch out
- `Queries` is no longer `Copy` or `PartialEq`, because it carries the counter.
- To drop an observation mid-pair in a rig test, `FanIo::hold_query` blocks the worker thread inside the fan SET on a std channel. The paused worker clock cannot hold it, since it auto-advances. After the drop, poll `observe` until the abandoned worker retires: until then a job starts no read.
- A heredoc with backticks and apostrophes still aborts the Bash call; write such scripts with the Write tool.
