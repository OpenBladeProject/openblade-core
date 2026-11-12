---
title: Preserve deferred display-off recovery when publishing profile outcomes
author: Jack
date: 2026-11-12
branch: fix/blade14-lighting-readback
touches:
  - src/OpenBlade.Service/ServiceControlRuntime.cs
---

## Decided
A full-profile lighting outcome marked Applied can intentionally defer host-renderer recovery while the display is off. Publication must check actual backend-aware ownership before treating that setting as successful for snapshot/queue cleanup. Preserve the newly queued destination retry when host ownership is None or Faulted, but clear stale host recovery after a genuinely accepted native effect.

## Tried and ruled out
Strict host ownership for every effect rejects native firmware controls. Unconditionally clearing queued recovery for every Applied status loses display-on restoration because display-off reconciliation deliberately retains Applied while scheduling the destination. Passing the existing device to full-transition publication allows the same backend-aware acceptance without inventing renderer ownership.

## Watch out
Keep PowerTransition_DisplayOffRestoresDestinationLightingOnDisplayOn for both None and Faulted in focused verification, together with failed-host-to-native and brightness-only recovery cases. A passing native-only fixture does not exercise the deferred display-off profile path.
