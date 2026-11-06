---
title: Await coalesced HyperBoost status callbacks before test commands
author: Jack
date: 2026-11-06
branch: feat/battery-saver-low-fan-validation
touches:
  - tests/OpenBlade.Tray.Tests/MainViewModelTests.cs
---

## Decided
The coalesced HyperBoost-entry performance-choice test must await the complete status-dispatch callback before directly invoking a view-model command. Reuse PublishStatusAndWaitAsync with a distinct Message for each snapshot. Preserve the skipped revision and the existing mutation/outcome assertions.

## Tried and ruled out
A full solution run exposed IndexOutOfRangeException in publishedComputedValues during disconnect/disposal. RecordingDispatcher already serializes its own actions, but polling SelectedPerformance or HyperBoostActive can wake the test before ApplyStatusCore finishes NotifyComputed. A direct test-thread command bypasses that dispatcher lock. Production WinUI command handlers retain UI context and watcher changes use the UI queue, so this observation does not justify a production dictionary or dispatcher change.

## Watch out
A property becoming visible is not callback completion. The disconnect stack can mask an earlier status-callback failure. Use the existing after-invocation barrier rather than sleeps or retries; keep the reproduced test repair separate from the offline guardian implementation.
