# Offline background helper benchmark

This Release console harness measures two current production helpers with fake inputs.
It performs no hardware operations, service IPC, installed-file inspection, GPU counter
enumeration, native audio/input calls, or `WindowsGpuAppManager` construction.

From the repository root:

```powershell
dotnet build build/benchmarks/BackgroundWorkloads/BackgroundWorkloads.csproj -c Release
$benchmarkHead = git rev-parse HEAD
dotnet build/benchmarks/BackgroundWorkloads/bin/Release/net10.0-windows/BackgroundWorkloads.dll --repo-root . --expected-head $benchmarkHead --output background-workloads.json
```

Run serially with other benchmarks. Exit zero means all semantic checks passed.
Timing, allocation, thread and handle results are observations without absolute pass/fail
thresholds. This measures offline helper behavior; it does not establish installed-product
idle power, GPU residency, firmware behavior, UI rendering or hardware performance.

## Production provenance

- The project references `OpenBlade.Contracts` and `OpenBlade.Platform` directly.
- The complete `src/OpenBlade.Service/StatusSubscriptionHub.cs` helper is source-linked.
  This avoids referencing the Service executable and its unrelated hosting/protocol graph.
  It measures the production hub algorithm, not the complete Service process.
- A single cached, exact-signature delegate calls the actual internal static
  `WindowsGpuAppManager.ReadActivitySampleAsync` in the Platform assembly. Reflection is
  limited to delegate binding before warmup. Linking the manager would pull in a large
  native process-management surface; no production visibility changes are required.
- Both production source files are embedded verbatim for SHA-256 provenance. At startup,
  their hashes must match the workspace, and their workspace diffs against HEAD must be
  empty. The report includes actual HEAD, source hashes, assembly hashes and module IDs.
  This permits benchmark-only uncommitted changes while rejecting modified measured code.

## Workloads and checks

One warmup set precedes three timed rounds (27 measured phases):

- Status hub: 100,000 increasing published revisions with 1, 4 and 8 stalled subscribers.
  Equal/older revisions cannot overwrite the latest value. After publisher completion and
  lease disposal, each reader must yield exactly one latest snapshot. A revision digest
  checks all deliveries. Filling remaining slots must reject subscriber nine; disposing
  all leases must restore all eight slots. The shared payload is a valid offline fixture;
  each published revision allocates a new snapshot record. A shared ten-second cancellation
  budget bounds reader enumeration if a future regression fails to complete a channel.
- GPU sampling: 1, 16 and 64 concurrent callers with entirely fake snapshots and awaited
  150 ms delays. Each successful caller must read exactly three samples and await twice;
  the final stable-process intersection, latest values, ordering and digest are exact.
  A fake asynchronous barrier holds the first delay until all callers are ready, so the
  pending checkpoint has no timing-dependent assertion. Successful waits then use real
  `Task.Delay` at the production-requested duration.
- GPU cancellation: the same concurrency levels cancel while all callers await their
  first fake delay. Every cancellation is observed. Each caller must have exactly one
  read and one delay, with no post-cancellation read. Cleanup cancels and observes every
  started task even if a check fails. Ten-second task timeouts are hang protection only.
  `CancellationToAllObservedCompletionMilliseconds` separately measures from immediately
  before token cancellation until every started task's completion has been observed. The
  whole-phase `WallMilliseconds` includes setup/checkpoints and is not cancellation latency.

The timed scope includes fixture/setup, semantic checks and cleanup, including the
explicit pending GPU resource checkpoint. Reports expose wall time, process CPU time,
process-wide allocated bytes, and boundary/pending threadpool, process-thread, handle,
working-set and private-byte samples. Process-wide allocations correctly include
continuations after `await` and also include harness overhead. Resource readings are
coarse snapshots rather than peak estimates. Full forced-GC retention checkpoints occur
before warmup, after warmup and after each round, outside all timed phases; no forced GC
runs inside a timed workload. Retained totals include the growing collection of report
objects and are not a leak attribution. Compare raw rounds under matching runtime and conditions.

See [recorded measurements and limits](RESULTS.md).
