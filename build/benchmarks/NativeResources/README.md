# Disconnected native resource validation

This opt-in harness measures real WinUI windows with isolated preferences and a
service client that rejects all calls before IPC. It runs on a private desktop,
using the same owned-process launcher as the native tests. It does not install
OpenBlade, open the product's pipes, observe input, or change hardware settings.

Build the candidate and runner with the same Release SDK/runtime and architecture:

```powershell
dotnet build src/OpenBlade.Tray/OpenBlade.Tray.csproj -c Release -p:OpenBladeDeveloperHarness=true -p:NuGetAudit=false
dotnet build build/benchmarks/NativeResources/NativeResources.csproj -c Release -p:NuGetAudit=false
dotnet run --project build/benchmarks/NativeResources/NativeResources.csproj -c Release --no-build -- "<absolute developer-harness OpenBlade.Tray.exe>" "<new evidence directory>" 300 5
```

The default run takes roughly ten minutes: five fresh-process startup probes,
then one process with warmup, five minutes idle, and five minutes paced activity.
A preliminary `5 1` run checks the harness without establishing sustained stability.
The evidence directory must be new or empty; reports are retained for inspection.
Optional arguments after the startup count are `dashboard|empty` and a 0-1000 ms
dwell after each window's loaded layout (defaults: `dashboard 0`). The empty-window
control keeps presentation activity but replaces dashboard creation with a plain
Window/Grid. These are diagnostic controls, not interchangeable workloads.
An optional final `--observe-rendering` subscribes to XAML rendering callbacks;
that subscription can request continuous frames, so use it only as a separate
diagnostic. Callback counts do not establish presentation to a physical display.
Before launch, the runner inspects the companion assembly metadata without loading
it and rejects ordinary product builds lacking the developer probe. It clears
inherited OpenBlade environment overrides, sets the phase budget and language,
and verifies hashes of every DLL/executable in the build output before and after
the run. Do not rebuild the candidate during measurement.

Startup is launch to **loaded layout**, observed by the parent with a monotonic
clock and 25 ms polling. It is an upper bound, not first paint. If the child exits
between polls, the exit-time upper bound is marked `readinessObservedAfterExit`.
The child also
records synchronous constructor time/UI-thread allocation separately. Fresh
processes use warm or uncontrolled OS caches; this is not a cold-disk startup test.
Snapshot mode disables normal observation and changes transitions/backgrounds,
so it is not installed-app startup or idle performance.

Activity adds timestamped synthetic samples to the bounded production history,
calls production presentation handlers, navigates eight pages, and repeatedly
creates, lays out and disposes quick-controls windows. It waits at least 250 ms
between operations; each accepted sample must cause exactly one chart rebuild
before navigation. Total rebuilds also include navigation. This is an
instrumented stress workload, not a claim about typical user activity or full
Service/Session Agent operation. The normal observer/IPC timers remain disabled.

Phase metrics use whole-process allocations and CPU; asynchronous continuations
do not use current-thread allocation accounting. Parent samples every five seconds
include private bytes, working set, handles, threads and CPU for the exact owned
child process. CPU is reported in milliseconds; dividing CPU milliseconds by
elapsed milliseconds gives a fraction of one logical core, not the whole machine.
GC collections are natural during timed phases. Forced-GC diagnostic checkpoints
occur only before/after those phases and after disposal. The main window and
fixture remain rooted, and weak-reference tracking/report objects themselves
consume memory. These checkpoints cannot prove complete window reclamation.
Separate weak references check retired dashboard windows/content and an
independently replaced main window. Each native window's `AppWindow.Destroying`
event verifies its closure without reusing a stale HWND. The regression pins
dashboard mode and requires every tracked managed window/content to be reclaimed.

Optional native diagnostics record current-process handle counts grouped by kernel
type and query kernel type metadata, never object names or other processes. The
x64 internal Windows layouts have bounded buffers, counts and retries; unsupported
layouts or query failures produce an explicit unavailable result. Their soft
elapsed-time budget does not interrupt a native syscall; the parent process
deadline remains the outer bound. Subscriber counts and native queries occur
outside timed phases. A clock regression invalidates timestamp acceptance.

No fixed timing or memory-growth threshold is a CI gate. Validate bounded history,
accepted samples, zero service calls, process identity and cleanup first. Compare
trends after warmup; if growth or slowdown is found, reproduce the affected phase
in alternating baseline/candidate order before attributing a regression. Stable
short-run memory/handles do not prove absence of long-term leaks. Process CPU and
memory do not measure GPU residency, battery power, energy or hardware latency.

See [recorded findings and remaining gaps](RESULTS.md).
