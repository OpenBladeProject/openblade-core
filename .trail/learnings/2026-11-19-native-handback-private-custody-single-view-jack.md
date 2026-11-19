---
title: "Native handback private custody, single view path and format vector"
author: Jack
date: 2026-11-19
branch: feat/native-managed-journal-handback
touches:
  - tests/Fixtures/Parity/native-handback-record-v1.json
---

## Decided
- Native handback records and their managed resolutions are published with the private-custody recipe (privateCustody: true). They name user SIDs and requested settings, so they must not inherit the namespace's Users ReadAndExecute ACE. The ordinary OpenChecked read accepts the private descriptor in both trust modes.
- The handback binding opens its files through WindowsNativeEpochFiles.ReopenLater(), which repins the root with the same exact-path, non-reparse and NTFS checks in the same trust mode. Tests and production use that one path; there is no separate test-only view.
- NativeHandbackStore is its own DI singleton over the handoff binding, and the runtime factory requests it first. A test pins the registration by invoking the real descriptor's factory with a provider that stops at the first request, so no hardware backend is constructed.
- A "continuing" resolution re-seeds the obligation as Confirmed. A claim made only of confirmed or live charges is ActiveChargeOnly, so a charge that ended before a restart is "reported" with no Battery latch, whichever slot it came from.
- tests/Fixtures/Parity/native-handback-record-v1.json is the known-answer vector for the history commitment, record commitment and record bytes. Custody recomputes the commitment at every start, so a format change needs a new versioned header, never an edited vector.

## Tried and ruled out
- Constructing the full runtime from AddServiceControlRuntime to pin the store argument: the factory builds hardware clients (for example the fan-stop guardian client) before the store is passed, so the probe must stop at the first request. A mutant passing null as the runtime argument after the request can still survive.

## Watch out
- ServiceMailboxHandoff's I/O-free constructor must stay I/O-free; admission happens only in Open, called once by the hosted ServiceMailboxCustodyStartup registered before Worker. Tests inject the admission through the internal Func constructor.
