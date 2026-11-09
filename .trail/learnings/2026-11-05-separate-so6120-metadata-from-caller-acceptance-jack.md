---
title: Separate SO6120 metadata from caller acceptance and writer handoff
author: Jack
date: 2026-11-05
branch: feat/battery-saver-low-fan-validation
touches:
  - docs/so6120-host-mailbox-contract.md
  - src/OpenBlade.Capture/So6120DirectDutyJournal.cs
  - src/OpenBlade.Capture/So6120SupervisedAdmission.cs
  - tests/OpenBlade.Capture.Tests/So6120DirectDutyJournalTests.cs
  - tests/OpenBlade.Capture.Tests/So6120SupervisedAdmissionTests.cs
---

## Decided

SO6120 supervised admission separates current machine metadata, trusted finite-caller acceptance and OpenBlade handoff evidence. Check the explicit experiment host/BIOS and reported EC version, exact readable HID control identity, current console/AC, configured pinned running driver and known controllers. A ready assessment does not open InpOut or authorize a command. Current-run freshness does not expire unchanged source or caller-challenge evidence by itself.

## Tried and ruled out

Do not treat a supplied evidence digest as authentication or a quiet trace as mailbox reservation. A matching SMBIOS version is not a running firmware image hash. The host helper's Tested label is an experiment-pin match, not newly performed physical validation. Read-only metadata collection sends no feature reports and uses no InpOut device handle. Checking only SCM registrations missed recognized orphan process aliases; use the shared Razer alias predicate over one process inventory.

## Watch out

Retain inaccessible relevant HID candidates and their native error, incomplete inventory, and the original structured host-refusal evidence. An already stopped own service requires no invented exited PID; a run that stopped it needs the original process identity and observed exit, plus a fresh stopped-service/no-writer observation in either case. The journal, policy and transport are still separate components, with live acquisition and one-owner composition unbound. Never manufacture challenge or handoff context just to make an assessment pass.
