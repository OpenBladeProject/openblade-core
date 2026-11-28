---
title: Keep installed mutation ambiguity in the original machine owner custody
author: Jack
date: 2026-11-12
branch: feat/installed-thermal-mutation
touches:
  - tools/openblade-windows-host/src/windows/scm/installed/durable_owner.rs
  - tools/openblade-windows-host/src/windows/scm/installed/mailbox_read.rs
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_storage.rs
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_native.rs
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_mutation.rs
  - src/OpenBlade.Deployment/WindowsNativeTrustLifecycle.cs
---

## Decided
The installed mutation route promotes the same authenticated read custody only after actual reader retirement. First ownership is Reserved then Active; a restart can read unresolved history but cannot fabricate a predecessor retirement. Keep the per-submission journal machine-global under that retained Store, while compatible saved overrides stay under the actual peer SID. Bind every command intent to the original request and owner before submission.

## Tried and ruled out
A per-user journal lets another SID bypass uncertainty. A new owner Store conflicts with the route's own exclusive handle. Retiring whatever Active record was loaded on restart invents a prior process's drain. Final status/subscription projection must bind private saved paths and overrides to the actual peer SID, including same-session RunAs callers. Metadata refresh must not erase accepted intent, but a stale retained read must not perpetually authorize writes. Exact Setup root ACLs also reject legitimate SYSTEM-owned user directories created with inherited ACLs.

## Watch out
The fixture composition must select every private input before default root/token suppliers can run. Preserve CustomManual unknown levels and the finite transaction pool across workers. A read-only reconciliation can describe firmware state without clearing unresolved owner or submission authority. Managed cleanup currently preserves and refuses the new journal even when transport retired; implementing its full shared consumer is separate recovery work. Offline source/compiler/fake-I/O proof is not live Windows or hardware proof.
