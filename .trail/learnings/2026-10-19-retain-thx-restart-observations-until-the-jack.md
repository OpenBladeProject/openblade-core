---
title: Retain THX restart observations until the next process intent
author: Jack
date: 2026-10-19
branch: feat/thx-driver-provisioning
touches:
  - src/OpenBlade.SessionAgent/ThxAudioProvisioningJournal.cs
  - tests/OpenBlade.SessionAgent.Tests/ThxAudioProvisioningJournalTests.cs
---

## Decided
A recorded THX 3010 result remains resumable when its post-restart observation is durable but the next process intent has not yet been written. The journal may therefore have either the preceding completed step or the restart step itself as its last observation. Fresh boot identity, exact installed-state checks and execution of only later unsubmitted steps remain required.

## Tried and ruled out
Requiring only the preceding observation stranded setup after a crash between the successful post-restart readback and the next intent. Do not solve this by replaying the submitted installer or accepting ordinary exit codes as restart continuations.

## Watch out
A new intent clears the prior process result. A crash after that new intent is still uncertain and non-resumable. Non-3010, missing-result and malformed records must remain paused. The regression tests cover all three restart-step checkpoints.
