---
title: Retain release-only origins across typed journal deferrals
author: Jack
date: 2026-11-14
branch: feat/windows-native-temporary-charge
touches:
  - tools/openblade-windows-host/src/service_status.rs
  - tools/openblade-windows-host/src/windows/scm/installed/battery_mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/battery_storage.rs
  - tools/openblade-windows-host/src/windows/scm/installed/mailbox_read.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/private_mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/status_source.rs
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_history_store.rs
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_native.rs
---

## Decided
Journal-capacity release returns Released, NoMatchingReservation or Deferred while the authoritative state mutex protects origin, settlement and accounting. Retain a Deferred origin as abandoned/release-only; an unsettled no-op is never proof of release. Existing serving-loop cadence retries only bookkeeping, and explicit requests remain truthfully blocked.

Classify retryable read unavailability at the concrete operation, not at an aggregate io::Error boundary. The explicit protected release adapter tags only raw relative-open sharing violation 32 and raw byte-read lock violation 33. Custody, ACL/identity, hashes, metadata, namespace, poison, revision and unknown errors retain their existing fail-closed paths. Release inspection uses active plus inspect and denies write methods; it never calls metadata recovery.

Publish bookkeeping changes through a public-only battery delta. Resolve observation age when publishing, preserve the existing owned request diagnostic and SID, and leave unrelated family degradation intact. The actual origin loop catches only the typed release deferral and retains its existing bounded cadence.

## Tried and ruled out
Calling the callback a second time after an error did not prove the serving loop survived. Returning Ok for busy storage discarded quota identity. Filtering all Other/error kinds or all nonfatal errors would turn trust or custody failures into retries. Ordinary journal_view can recover metadata, so it is not a strict read-only release path. Ordinary battery_projection replaces a request diagnostic; avoiding SID rebinding alone did not preserve that diagnostic. The battery test request helper uses enabled for non-policy calls and must not build performance requests.

## Watch out
Use deterministic barriers through the actual native policy worker/storage seam: independent false, worker Prepared before release lock, Deferred identity retained, worker settlement, later actual-loop release without setter replay. Exercise serving-loop status and typed request handling plus real fatal/trust/custody controls. Preserve the prospective-worker, cross-SID, same-SID fresh-session recovery and original fan/ID fences. Required frame/own-SID Facts remain fail-closed; selected facts are not an ordinary-suite or CI-green claim.
