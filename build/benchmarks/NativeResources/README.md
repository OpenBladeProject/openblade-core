# Disconnected native resource validation

This opt-in harness measures real WinUI windows with isolated preferences and a
service client that rejects all calls before IPC. By default it runs on a private desktop,
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
Optional arguments after the startup count are a window kind and a 0-1000 ms
dwell after each window's loaded layout (defaults: `dashboard 0`):

| Kind | Window/presenter and permanent-close behavior |
| --- | --- |
| `dashboard` | Actual quick-controls window, including shared production lifetime cleanup |
| `dashboard-empty` | Actual dashboard construction/presenter/subclass/timers, then replace its content with Grid; track the detached content too |
| `empty` | Plain Window/Grid with the default presenter |
| `empty-toolwindow` | Plain Window/Grid with the dashboard's tool-window presenter and hidden native title bar |
| `empty-toolwindow-titlebar` | Same tool-window recipe, retaining the native title bar |
| `empty-toolwindow-restore` | Hidden-title-bar tool window; Hide, restore `hasTitleBar: true`, then Close |
| `empty-toolwindow-reset` | Hidden-title-bar tool window; Hide, `TitleBar.ResetToDefault()`, then Close |

These are diagnostic controls, not interchangeable workloads. The plain tool-window
controls intentionally express their presenter and cleanup calls literally, independent
of the shared production helper. This preserves the mechanism comparison when production
cleanup changes; it is not silent drift. Historical `dashboard-reset` reports are retained,
but that experimental kind was removed after production adopted shared cleanup. Use
`dashboard` to exercise the current production dashboard path.

The pinned project uses Windows App SDK Runtime 2.2.0 and WinUI 2.2.1. Its
`SelfContained=false` selects a shared .NET runtime; the independent
`WindowsAppSDKSelfContained=true` carries the Windows App SDK runtime with the app.
Do not describe this as a framework-dependent Windows App SDK measurement merely
because the .NET deployment is framework-dependent.
An optional final `--observe-rendering` subscribes to XAML rendering callbacks;
that subscription can request continuous frames, so use it only as a separate
diagnostic. Callback counts do not establish presentation to a physical display.

For a matched desktop comparison, add `--offscreen` to both runs and
`--input-desktop` to the second run. The latter requires `--offscreen`; it never
falls back to the activating snapshot path. Before any show, the dedicated path
initializes generated bindings, applies native input guards, positions the entire
window outside the complete virtual screen, and uses `AppWindow.Show(false)`.
It checks geometry, thread-local activation and foreground ownership before/after show, after loaded layout
and after dashboard disposal. Input-desktop mode must freshly verify that the UI
thread belongs to the current input desktop; private mode requires the launcher's
exact owned desktop name. Query unavailability rejects input-desktop mode. Keep
the input desktop unlocked throughout that run; locking or switching to a secure
desktop invalidates the comparison and stops it. Private tests
can record unavailable input-desktop metadata without leaving their private desktop.
The runner rejects older harness binaries missing this offscreen helper before
launch. Both modes keep the rejecting client and per-run preferences.

```powershell
# Same executable, dimensions, fixture, dwell and phase budgets; run serially.
dotnet run --project build/benchmarks/NativeResources/NativeResources.csproj -c Release --no-build -- "<harness exe>" "<new private directory>" 30 1 dashboard 0 --offscreen
dotnet run --project build/benchmarks/NativeResources/NativeResources.csproj -c Release --no-build -- "<same harness exe>" "<new input directory>" 30 1 dashboard 0 --offscreen --input-desktop
```

This comparison isolates desktop mode under the same offscreen workload. Neither
run establishes visible-window presentation, normal installed operation or energy.
Timed activity includes the fresh desktop, geometry and activation checks. Compare
matched offscreen modes; their instrumentation differs from legacy snapshot mode.
Before launch, the runner inspects the companion assembly metadata without loading
it and rejects ordinary product builds lacking the developer probe. It clears
inherited OpenBlade environment overrides, sets the phase budget and language,
and verifies hashes of every DLL/executable in the build output before and after
the run. Do not rebuild the candidate during measurement.

Startup is launch to **loaded layout**, observed by the parent with a monotonic
clock and 25 ms polling. It is an upper bound, not first paint. If the child exits
between polls, the exit-time upper bound is marked `readinessObservedAfterExit`.
The child also records synchronous constructor time/UI-thread allocation separately.
The child's loaded-layout timestamp precedes the final presentation check; the
parent's readiness upper bound includes that check.
Fresh processes use warm or uncontrolled OS caches; this is not a cold-disk startup test.
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
outside timed phases. GDI and USER counts use current-process GUI-resource queries
after warmup and after the activity collection, also outside timed phases. Query
errors or unavailable counts remain explicit; they are not converted to zero.
These native/GDI before readings occur before the idle phase, while each timed
phase has its own later process-counter start. Do not subtract a native after-warmup
count from an activity-start process count. A clock regression invalidates timestamp acceptance.

The shared production `WindowTitleBarLifetime` cleanup applies only at permanent
disposal of both XAML window roots. Short pinned-runtime evidence supports hiding
before resetting native title-bar state; the custom MainWindow path additionally
detaches its XAML title bar and disables extension. This is separate from ordinary
hide/show. Native-before-close marker assertions have passed, and omitting dashboard
cleanup makes both native regression cases fail. Failed-initialization coverage also
reproduced the old skipped-cleanup path. A throwing cancellation callback additionally
verifies that synchronous cleanup failures do not skip later teardown. All 2,650
Tray tests and the final 600-second idle/activity workload passed those lifetime
checks; [the results](RESULTS.md) retain the remaining native growth and validation
limits. No product GC or forced COM release is involved.

No fixed timing or memory-growth threshold is a CI gate. Validate bounded history,
accepted samples, zero service calls, process identity and cleanup first. Compare
trends after warmup; if growth or slowdown is found, reproduce the affected phase
in alternating baseline/candidate order before attributing a regression. Stable
short-run memory/handles do not prove absence of long-term leaks. Process CPU and
memory do not measure GPU residency, battery power, energy or hardware latency.

See [recorded findings and remaining gaps](RESULTS.md).
