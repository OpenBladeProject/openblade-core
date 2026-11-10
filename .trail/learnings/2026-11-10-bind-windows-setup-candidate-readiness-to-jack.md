---
title: Bind Windows Setup candidate readiness to the incoming service
author: Jack
date: 2026-11-10
branch: feat/windows-setup-candidate-readiness
touches:
  - src/OpenBlade.Setup/SetupServiceHealthProbe.cs
  - src/OpenBlade.Setup/SetupCandidateServiceVerification.cs
  - src/OpenBlade.Setup/WindowsSetupCandidateService.cs
  - src/OpenBlade.Setup/WindowsSetupPlatform.cs
---

## Decided

Candidate-only Setup readiness should bind the existing typed status exchange to the exact incoming launch. Query the fixed OpenBlade SCM registration independently and check the same local pipe handle and caller-retained process and pre-launch build evidence before and after the exchange.

## Tried and ruled out

The existing timing PID is sampled for diagnostics. It cannot establish which process served a trusted status. A named pipe alone, an SCM RUNNING state, and a path hash taken after launch also leave the process generation or loaded image unbound.

## Watch out

The transition owner retains the original process handle and the pre-launch image pin across readiness. A new handle opened by PID or a new hash of the path cannot replace that evidence. Keep the managed install and rollback probe behavior independent of candidate admission. A complete response followed by ordinary one-shot server closure can succeed if same-handle peer identity and the retained live service still verify.
