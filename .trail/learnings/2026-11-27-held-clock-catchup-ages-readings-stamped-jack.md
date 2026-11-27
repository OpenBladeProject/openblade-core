---
title: Held-clock CatchUp ages readings stamped during the exchange
author: Jack
date: 2026-11-27
branch: fix/native-realtime-rigs
---

## Decided
- prepared_race's policy exchange ends with HeldClock::Keep (source_mutation_then in temporary_charge_release.rs). Its job stamps the policy reading on the held clock, which stays put while the job runs, and the confirmed battery group is read just after.
## Tried and ruled out
- CatchUp on that exchange: it moves the clock by the whole real length of the exchange, so on a slow host (5 s or more) the reading falls outside mailbox_read::RETENTION_WINDOW and the confirmed battery group comes back Null.
## Watch out
- Signature: `published["confirmedState"]["battery"]["chargeLimitPercent"]` is Null instead of 70 at temporary_charge_release.rs (prepared_race), in tfc_release_actual_worker_prepared_race_... or tfc_release_outcome_cause_changes_publish_... . To reproduce, stall the job thread 6 s or more in the "prepared" hook.
- Any held exchange that ends with CatchUp and is followed by a freshness-bounded read has the same exposure. Use Keep when the assertion reads what that exchange stamped.
