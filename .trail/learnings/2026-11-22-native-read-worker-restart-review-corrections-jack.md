---
title: "Native read-worker restart, review corrections"
author: Jack
date: 2026-11-22
branch: fix/native-read-worker-after-drain
touches:
  - tools/openblade-windows-host/src/windows/scm/installed/mailbox_read.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/read_worker_restart.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/mutation_fixture.rs
---

This supersedes parts of
`2026-11-22-native-read-worker-restarts-after-a-drain-jack.md` after review.
Each superseded statement is quoted.

## Decided

- Supersedes "A refused fresh write admission or the low-power latch does not
  hold reads back once read custody admits them." While the low-power gate is
  set, read custody itself refuses: `ReadLease::authorize`
  (`mailbox_read.rs`) needs `lifecycle.active()`, which is the Ordinary
  write-scope epoch and is absent in low power. So no read job runs and no
  reader is rebuilt until resume; after resume the next read rebuilds it
  without a mutation. Only a refused fresh write admission outside low power
  leaves reads running at once.
- Supersedes "the read request deadline bounds that wait." After a canceled
  drain whose reader still has an abandoned job, the next observeState runs
  the restart and waits in its stop for that job, up to its own 2 s deadline,
  and confirms nothing. Reads rebuild only after the job retires.
- An unconfirmed reader stop stays sealed: the restart propagates the stop's
  error before it rebuilds anything, and the worker's unconfirmed state is
  sticky, so a cleared fault does not unseal it. A fenced source refuses every
  later mutation without a second activation, before and after a drain.
  `read_worker_restart` now kills both mutants (ignoring the stop result;
  dropping the fence check).

## Watch out

- The mutation fixture's `read_worker_error` fails a read job after its port
  retires, which is how an unconfirmed reader stop is modelled. The unscoped
  rig refuses fan and battery requests at `fresh.authorize()`; production
  refuses earlier, at capture, which `scoped_fresh` models.
- Offline injected-rig evidence only; no installed or hardware validation.
