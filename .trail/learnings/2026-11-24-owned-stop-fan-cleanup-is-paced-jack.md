---
title: Owned Stop fan cleanup is paced to the source drain (supersedes the keyboard-only suspend learning)
author: Jack
date: 2026-11-24
branch: feat/native-low-power-driver-release
touches:
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/fan_mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness.rs
  - tools/openblade-windows-host/src/windows/scm/installed/status_source.rs
  - tools/openblade-windows-host/src/windows/scm/installed/driver_mode_owner.rs
---

This supersedes `2026-11-24-native-suspend-is-keyboard-only-generation-jack.md` on how owned-Stop fan cleanup is paced, which deadline it uses, what resolving an ambiguous release may claim, and what happens to a leftover 31/4x tail. Where the two disagree, this one holds. The rest of that learning stands.

## Decided

- `Pace::covers_before_rows` charges every row left, the 31 included, at the second longest exchange (`measured.second`), not the slowest. That matches Pace's one-stall rule. With the slowest, one 400 ms stall refused a handback that needed about 0.7 s of its 3 s. A device that is slow throughout still has two slow baseline exchanges (0D/82 and 07/8C) and is still refused.
- The owned-Stop handback is paced to what is left of the source drain. `Ports::retire` computes one deadline from `SOURCE_DRAIN_TIMEOUT` (2 s, or 2.9 s with the keyboard share), uses it for its `timeout_at`, and passes it through `StatusSource::drain_by` to `stop_fans` and `run_owned_stop`. The default `drain_by` calls `drain`, so the 28 other StatusSource impls are unchanged. `GuardedStatus::drain` is `drain_by(now + SOURCE_DRAIN_TIMEOUT)`. Each cleanup job is also bounded by its own 3 s (`MAX_OBSERVATION`, through `worker.observe`), so the old 3 s deadline in `run_owned_stop` was removed rather than kept as a minimum.
- When a confirmed read resolves an ambiguous release, a Driver result keeps whatever claim exists and never creates one: `if !driver { owns.store(false) }`. Only an acquire's own setter claims.
- A 31/4x tail left by Stop is not settled at the next start. A restarted Service finds the row unsettled and refuses; nothing settles it yet. The earlier learning's "next start recovers it read-only" was wrong.
- Native hands fans back at Stop after a resume, an active-user change or a competing controller's visit. In those cases managed drops ownership and refuses (ServiceControlRuntime.cs:13243, 1589, 15707, 21396, 13666-13676). This divergence is kept on purpose, because returning the fans to firmware is the safe direction. It is recorded in docs/windows-rust-native-fans.md.

## Tried and ruled out

- Changing the `StatusSource::drain` signature to take the deadline. There are 28 impls and about 40 test call sites, so `drain_by` with a default is used instead.
- Computing the deadline inside `GuardedStatus::drain` alone. That would skew it past retire's timeout by however long the source lock takes.

## Watch out

- The fixture's `latency` applies to every transfer, so one exchange costs 2 × latency plus a 30 ms settle. "180 ms per exchange" means `latency = 75 ms`.
- The fixture starts each job's paused clock only when its worker thread starts. On a loaded host that was 0.35-0.95 s after `observe()` computed the job's 3 s bound, so a timing test needs about 1 s of margin on its admitted side. The drain-pacing tests use 100 ms exchanges (about 1.5 s) for windows of 1 s and 4 s, and 150 ms exchanges (about 2.25 s) for the fan-only 2 s drain.
- `fan_v2_owned_stop_cleanup_gets_the_drain_bound` runs only without the keyboard owner, because with the keyboard share the bound is 2.9 s. CI's fan lane runs it through the `fan_v2_owned_stop_cleanup` filter.
