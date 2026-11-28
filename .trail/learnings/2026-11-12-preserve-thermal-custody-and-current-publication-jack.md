---
title: Preserve thermal custody and current publication truth
author: Jack
date: 2026-11-12
branch: fix/native-thermal-review-corrections
touches:
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_mutation.rs
  - tools/openblade-windows-host/src/scm_host.rs
  - rust/crates/openblade-domain/src/thermal_mutation.rs
---

## Decided
Keep installed loaded-build Origin custody immutable across ordinary power and session notifications. The shared installed_ingress action revokes it only for terminal STOP/SHUTDOWN; routine ingress fences in-flight work with a separate sticky lifecycle epoch. Fresh mutation admission independently checks the exact active console session's WTSInfoEx active/unlocked state. This stage compiles those Windows suppliers but never executes them.

Exact RZ09-0581 performance-only parity on PluggedIn is Stored/no setters when current fans and effective desired fans are manual. Automatic-active plus saved fixed fan intent still transitions directly. This supersedes the earlier fixed-fan per-preset restoration attribution; do not add experimental per-preset fan restoration to 0581. Generic manual observations retain unknown preset/custom details honestly.

Private intent is re-resolved against current public metadata and bound to SID. ExternalUnknown is not a confirmed power transition and cannot expire a temporary override or establish full-power effectiveness. Stored publication merges private fields onto latest public hardware under the publication lock and never renews another operation's read witness. Late accepted intent survives a canceled result through scoped persisted-policy resolution without replay.

Prepared-only settlement is KnownNotSubmitted only after actual worker/transport retirement and exact authenticated request/owner/user/prefix checks. FirstIntent and later ambiguity stay fenced. Clean known-unpublished activation refusal alone can reconstruct the reader; uncertain custody never does. Initial read admission precedes ID reservation, and already-reserved IDs remain burned.

## Tried and ruled out
A display-on prerequisite would contradict managed ServiceLifecycleRefreshLoop.cs:134-138: display-off is not proof of Modern Standby. Preserve the managed LowPowerGate initial clear latch and explicit suspend/resume boundary; initial activity is not claimed as an independent awake measurement. An unrelated session notification cannot resume a suspended lifecycle.

Do not retain whole private snapshots or infer submission from failed configuration replacement. Do not use a component-only lifecycle test: the outer Ports composition uses the same production ingress action and real immutable Lifetime with injected I/O. This learning records implementation decisions; passing compile or injected tests does not establish installed runtime or hardware behavior.

## Watch out
The IPC80 import is a closed 12-file dependency set from b8dcb7bef9c8fed72796888b011349d9bb12150a, deliberately merged with migration defines and confirmed-Custom/null-settings validation. Requests and desired state remain strict, legacy51 is read-only projection,79 is rejected, and embedded Blade14 layout CRLF bytes remain hash-bound. Broader current-main lighting/backend work is intentionally outside this stage.
