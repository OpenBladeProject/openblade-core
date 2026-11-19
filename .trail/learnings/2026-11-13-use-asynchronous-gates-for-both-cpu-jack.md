---
title: Use asynchronous gates for both CPU snapshot lock tests
author: Jack
date: 2026-11-13
branch: fix/installer-failure-details
touches:
  - tests/OpenBlade.Protocol.Tests/Rz090528CpuLiveQueryTests.cs
  - tests/OpenBlade.Protocol.Tests/WindowsRz090528CpuNativeSessionTests.cs
---

## Decided
Follow-up to the mailbox snapshot learning: apply the same dedicated query/snapshot workers to both PendingMailboxWaitFreezesDistinctPhaseAndZeroCounts and ProgressCanFreezeWhileInstalledBoundaryIsPending. Use asynchronous TaskCompletionSource entered/release signals, keep the two-second snapshot deadline, and release only in finally. Bound the combined worker join and retain both the original assertion and a cleanup failure in AggregateException.

## Tried and ruled out
Claude identified the identical sibling harness in WindowsRz090528CpuNativeSessionTests. Fixing one queued snapshot would leave the same dependency in the other path. Manual-reset-event disposal after a timed-out join could race a late worker, so the final harness owns no disposable wait events. Avoid a synchronous entered wait on the async test worker. The generated learning date follows UTC; no date rewrite is needed.

## Watch out
A cleanup timeout does not prove task termination. Preserve that failure rather than claiming successful cleanup or masking the original assertion. Both tasks access only isolated fake state; neither signal is disposed under an uncertain worker. Production query code, progress locking and deadlines remain unchanged. A fake that automatically releases can let a real snapshot-lock bug pass.
