---
title: Native display-brightness HID input on RZ09-0581
author: Jack
date: 2026-11-26
branch: feat/native-brightness-hid-usage
---

## Decided
- Native `setDisplayBrightnessHidUsage` (feature `service-brightness-hid-usage-v1`) ports managed's held-usage state machine as is; the managed vectors in `tests/data/display-brightness-hid-vectors.json` are scripted at the `IDisplayBrightnessHidTransport` level, so the transport's path cache is tested separately (`Transport`), not through the vectors.
- A press is recorded as held *before* its IOCTL starts. Only a Blocked reply (nothing submitted) restores the prior state; Failed, an over-budget submission and a dropped request after the lane started the call all stay held and are never resent.
- All Windows calls run on one lane thread with a 2-slot queue; a queued call the caller stopped waiting for is withdrawn (QUEUED->WITHDRAWN CAS) so it never starts later.
- Installed driver package trust lives in the host crate root (`driver_trust`, feature `driver-api-trust`), not the platform crate, to avoid widening the Check-Workspace boundary.

## Tried and ruled out
- Persisting the held usage: managed persists nothing; a restart neutralizes before the first press instead.

## Watch out
- Native refuses SYSTEM for this request (managed does not check the caller); the Write capture needs the caller's SID.
- No low-power release or maintenance-tick neutralization yet; RZ09-0528's stack is not resolved (publishes Unavailable).
- `Outcome` only derives Debug/PartialEq under cfg(test).
- trail check on this landing base flags .editorconfig/.gitattributes/workflow headers from the base's divergence from main, not from this change.
