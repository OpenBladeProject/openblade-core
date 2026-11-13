---
title: Keep overlap assertions independent of the owner response deadline
author: Jack
date: 2026-11-13
branch: fix/battery-protection-963
touches:
  - tests/OpenBlade.Capture.Tests/CpuPowerProbeResponsesTests.cs
---

## Decided
The issue 965 quality gate failed only CpuPowerProbeResponsesTests.QueryValueAsync_OverlappingGetterIsRefusedWithoutReplacingTheActiveQuery: the owner timed out before the test published its reply. That concurrency test gave the first query a one-second real deadline while awaiting overlap assertions. Keep its owner under explicit cancellation with an infinite response deadline during those assertions, retain a ten-second bounded completion wait, and cancel in finally. Production response timeouts and dedicated expiry tests remain unchanged.

## Tried and ruled out
Do not retry the full CI blindly or weaken production probe deadlines. The failure is a test scheduling race, not evidence that the battery correction broke CPU reads. The local five-minute Capture/Service budgets also did not prove hangs: completed SilentTrial cases consumed292 seconds through real journal flushes and timers. Hosted Service tests passed all3907 cases.

## Watch out
Record the initial native Claude-reviewed head b4e865b0 and the verified production corrections f860363e separately from this test-only gate correction. No new hardware authority or physical recovery claim follows.
