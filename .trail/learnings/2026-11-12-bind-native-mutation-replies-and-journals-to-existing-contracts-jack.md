---
title: Bind native mutation replies and journals to existing contracts
author: Jack
date: 2026-11-12
branch: feat/installed-thermal-mutation
touches:
  - tools/openblade-windows-host/src/service_status.rs
  - tools/openblade-windows-host/src/windows/scm/installed/durable_owner.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/mutation.rs
  - tests/OpenBlade.Contracts.Tests/NativeThermalMutationResponseTests.cs
---

## Decided
Native v79 mutation success carries only ControlOperationResult. Final status is a separate GetStatus, ObserveState or subscription frame. Verify emitted replies through the unchanged managed codec, including separate confirmed status. Validate every thermal journal owner nonce against actual Active owner history retained by the same Store; legitimate prior confirmed owners remain recognized after the next owner activates.

## Tried and ruled out
A native response containing both status and result violates the managed single-body contract even when each body is individually valid JSON. A syntactically valid owner digest alone does not bind a journal to authenticated custody. A previous reader-only owner-loss test does not cover mutation-stage loss after a setter or before final publication.

## Watch out
Exercise second-command submission failure, final read timeout/malformed reply and custody loss before the second command or final publication through the installed mutation route. Keep original request/progress and actual callback retirement assertions. A durable Confirmed result can still lose current publication authority. Preserve incomplete journal history and never infer prior retirement from process exit. CustomManual High/High confirms Custom plus Maximum fans while CPU/GPU levels and full custom settings remain unknown.

The owning shared codec needs separate confirmed-observation validation: a known Custom selector may have unknown full settings. Desired/requested Custom stays strict and malformed present confirmed settings fail. Keep the actual emitted partial observation in the managed bridge; separate HyperBoost plus null-capability decoder parity remains unresolved.
