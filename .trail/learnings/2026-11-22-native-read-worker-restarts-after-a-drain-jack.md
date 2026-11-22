---
title: Native read worker restarts after a drain without a mutation
author: Jack
date: 2026-11-22
branch: fix/native-read-worker-after-drain
touches:
  - tools/openblade-windows-host/src/windows/scm/installed/mailbox_read.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/read_worker_restart.rs
  - docs/windows-native-thermal-mutation.md
---

## Decided

- Every stop of the installed reader by a mutation entry point goes through
  `ReadSource::stop_reader`. It sets `MutationPhase::ReaderDrain` before it
  awaits the stop, unless the source is `Fenced`. The only restart point stays
  `drain_mutation(true)`, which the next observation or `mutate` runs: it
  rebuilds the reader after a confirmed stop, fresh integrity and read
  custody.
- `drain_active` (battery, temporary-charge Battery event, fan and a
  reconciliation's fan step) and owned-Stop `mutate_cleanup` used to stop the
  reader with the phase left `Idle`. A caller that then returned without
  `mutate` (a blocked refusal, a paused Battery event, a refusal in low power)
  left every later `observeState` and subscription read on a stopped worker,
  which answers `Ok(None)`, until a mutation rebuilt it. `mutate` itself was
  already correct: it retained the phase before its own stop.
- Read restart needs read custody only. A refused fresh write admission or
  the low-power latch does not hold reads back once read custody admits them.

## Tried and ruled out

- Restarting the reader at the end of `drain_active`: its callers run their
  own admission after it returns and `mutate` stops the reader again, so an
  eager restart only adds a worker; the lazy restart already runs under the
  `operation` serialization and after a confirmed stop.
- Patching each early return in the battery and fan entry points: those files
  belong to other work, and the same defect would return with the next caller.

## Watch out

- A `Fenced` phase must never be overwritten by the restart phase: an
  uncertain activation may have published an owner. The mutant that sets the
  phase unconditionally is killed by
  `a_drain_of_a_fenced_source_keeps_reads_sealed`.
- A reader that is still running an abandoned job blocks the restart until
  the job retires; the read request deadline bounds that wait.
- Offline injected-rig evidence only; no installed or hardware validation.
