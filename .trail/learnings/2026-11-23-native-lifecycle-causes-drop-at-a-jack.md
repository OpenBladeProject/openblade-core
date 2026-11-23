---
title: Native lifecycle causes drop at a competing controller and pace their retries
author: Jack
date: 2026-11-23
branch: feat/native-power-lifecycle-reconcile
---

Supersedes 2026-11-23-native-lifecycle-causes-outlast-refusals-and-jack. A re-review found that a cause outlasting every refusal also waited out a competing controller and then wrote. It also found that a busy tick erased a real sleep gap, and that a burst of notices could use up a debt's budget.

## Decided
- A capture refused because fresh competing-controller absence is not confirmed drops the lifecycle cause and settles the pair's debt.
  - The refusal is typed: `Identity::admits` returns `Denial::CompetingController` from its controller check only, and `WindowsGuard::capture` maps it to `thermal_mutation::competing_controller()`. The lifecycle tick asks `is_competing_controller`. No string matching is involved.
  - A detection warning or an inventory slower than 1 s also confirms no absence. Managed treats a detection warning as a controller, so both drop.
  - Managed matches: `powerToBatteryWithController` and `controllerClosedPowerUnchanged` in lifecycle-reconcile-vectors.json both submit nothing.
  - The debt is settled, not left owed, because an unchanged report pays an owed debt and would write once Synapse closes.
- Every other refusal before a step keeps the cause: a locked or switched session, no console user, unknown power, a fenced store, or a per-user store that cannot be opened.
  - A fenced store is OpenBlade's own unsettled row, and `admit_trigger` refuses any replay.
  - The native Service never creates a missing store directory. Only a Service switch provides one, and that starts a new runtime.
- A retry after steps waits `PACE` (2 s) after them as well as for an epoch move (`Pending::stepped`). Epoch moves inside the window only mark the cause due. A refusal before any step is not paced.
- A busy tick stamps its time only while the gap since the last stamp is under `SLEPT`.
- The console probe is never read in low power. While no user is named it is read every `UNNAMED_POLL` (3 s) at an unmoved epoch. The read epoch and time live in `Console.read`; `Tick` no longer has an epoch.
- The readiness self-probe's status read creates the status feed's publisher before the serving loop starts, so the first tick after RUNNING can write. Managed also writes at start.

## Tried and ruled out
- Keeping the cause across a controller refusal. Synapse open at logon or unplug, a mode chosen there, and Synapse closed an hour later meant OpenBlade overwrote that choice at the next battery-level notice.
- Dropping the cause but leaving the debt owed. The reporter's next renewal report pays an owed debt, so the same overwrite happened through the report path.
- Classifying a missing store directory by `ErrorKind` to drop it. That kind is not unique to the directory, and the directory cannot appear within one runtime anyway.

## Watch out
- A lifecycle attempt takes several captures (the tick's and each step's), so assert `captures > before` for an attempt, not `+ 1`. Only a refused tick capture is exactly `+ 1`.
- `age_lifecycle_tick` also ages the console read time and the last stepped attempt. Use it to cross `PACE` and `UNNAMED_POLL` in tests.
- `lifecycle_pair` reads the console probe too, so the probe counts in `probed()` include one read per attempt.
- The rig's `Custody.controller` flag models the typed refusal. `WindowsGuard::capture`'s mapping is compile-only offline.
- A controller that appears between the attempt's capture and a step's write refuses that step, which spends one of `RETRIES`. The next attempt's capture drops the cause.
