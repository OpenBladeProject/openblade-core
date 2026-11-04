# Native resource validation, 2026-11-03 to 2026-11-04

Release, .NET 10.0.12, Windows build 26300, x64, en-US, 150% scale. The
production starting revision was `295d7e6d`; the opt-in probes were added in the
working tree. Every run records the actual binaries independently of that product
version string. Measurements were serial, with no concurrent builds or other
benchmark processes. Ordinary host load and OS caches were uncontrolled.

## Confirmed lifetime defect

Five fresh-process startup probes followed a short pilot, then a single process
ran five minutes idle and five minutes of synthetic presentation and repeated
quick-controls creation/disposal. The baseline produced 965 sample operations,
1,025 chart rebuilds and 100 dashboard cycles, with zero service calls and a
bounded 3,600-sample history. During activity, private bytes rose from 205,578,240
to 536,432,640 and handles from 1,384 to 5,926. All 100 closed dashboard wrappers
survived the final forced-GC checkpoint. The periodic observations are in
[the baseline series](results/2026-11-03-baseline-series.csv).

A shorter diagnostic established that native HWNDs were destroyed and the
application's static `RevealHosts` table was empty, while both window and content
objects survived. The generated root `x:Bind` connector subscribes its `Activated`
method to the Window, but `StopTracking()` does not remove that subscription.
This matches the reported [upstream WinUI issue #9960](https://github.com/microsoft/microsoft-ui-xaml/issues/9960).

Permanent disposal now removes that exact generated delegate, then stops binding
tracking. The shared helper applies to MainWindow and TrayDashboardWindow, the
project's two Window XAML roots. It leaves hide/show behavior and independent
subscriptions intact. It uses the exact public signature because the concrete
generated class is unavailable during XAML's first compilation pass. No private
event storage, forced COM release, runtime GC or firmware behavior is changed.

The same 30-second idle/activity workload was run in baseline/candidate/baseline
order. The final baseline reused the preserved original DLL; hashes and phase
metrics are in [the comparison](results/2026-11-03-lifetime-comparison.json).

| Run | Dashboard cycles | Windows/content surviving collection | Valid old HWNDs | Static host entries |
| --- | ---: | ---: | ---: | ---: |
| Baseline | 14 | 14 / 14 | 0 | 0 |
| Candidate | 14 | 0 / 0 | 0 | 0 |
| Restored baseline | 14 | 14 / 14 | 0 | 0 |

The original comparison checked old HWND validity. The final regression instead
records each window's destruction event, avoiding handle-reuse ambiguity.

This comparison establishes the retained-object correction. Natural GC/native
release timing still affects phase-end private bytes and handles; collection
checkpoints and periodic resource trends are reported separately. A further
short pilot with the shared fix reclaimed all six dashboard windows and one
independently replaced MainWindow, including their content graphs.

The corrected five-minute idle/five-minute activity run completed 1,028 sample
operations, 1,092 total chart rebuilds and 107 dashboard cycles. All dashboard
windows and content were reclaimed, as was the independently replaced main
window. Managed retention after collection fell from 4,531,960 to 4,373,448 bytes.
The [candidate series](results/2026-11-03-candidate-series.csv) and
[candidate summary](results/2026-11-03-candidate-summary.json) preserve the result.
This run predates later diagnostic/error-handling refinements; it is evidence for
the shared lifetime fix, not a timing result for the final commit.

## Historical native-resource growth before title-bar attribution

The same candidate activity still increased handles from 1,442 to 5,635 and
private bytes from 209,924,096 to 372,449,280. After collection and a 250 ms
dispatcher delay, they were 5,635 and 371,228,672. Reclaiming managed windows
therefore does not establish complete native resource reclamation.

Bounded follow-ups isolated parts of this behavior:

| Control | Total window cycles | Handles at activity start / after collection | Result |
| --- | ---: | ---: | --- |
| Plain Window/Grid, 30-second phases | 16 | 1,316 / 1,368 | Much less growth; content/presenter/timer differences remain |
| Clear dashboard Content before Close, 30-second phases | 15 | 1,395 / 1,927 | No correction; experiment removed |
| Dashboard with 250 ms dwell, 15-second phases | 9 | 1,406 / 1,590 | Growth persists after waiting beyond loaded layout |
| Same dwell plus explicit rendering observer | 9 | 1,487 / 1,730 | 902 idle / 854 activity callbacks; growth persists |

The [control reports](results/2026-11-03-controls.json) retain phase metrics,
diagnostics and binary/report hashes. Subscribing to rendering changes frame
demand, so the last control's CPU is not comparable with default idle measurements.
The callbacks establish that XAML rendering callbacks occurred, not physical
presentation or native composition retirement.

Shared view-model native subscriber counts decreased after collection (32 to 26
in one diagnostic), rather than accumulating. Current-process native type counts
showed most growth in `Section` and `DxgkCompositionObject`, with a smaller `Event`
increase. These names come from queried type metadata, not hard-coded index labels.
The pinned WinUI package is 2.2.1 on Windows build 26300; these initial measurements
ran on inactive private desktops. At this stage, attribution to application content,
WinUI or composition was unresolved. The later title-bar controls below narrow that
boundary. No forced native release, production delay or package upgrade was
introduced on the initial evidence.

### Matched offscreen desktop controls

A subsequent diagnostic uses explicit generated-binding initialization and
`AppWindow.Show(false)`, with every window positioned wholly outside the virtual
screen before showing. Native input guards, fresh desktop identity checks and
process-wide foreground rejection remain active. The private pilot passed before
the input-desktop pilot; the runner also rejected an older resource harness lacking
this mode before launch. Private mode requires the shared launcher's isolated
desktop name; input mode must freshly match the current input desktop and has no
fallback. No installed service, input hook or hardware mutation was involved.

Two serial runs used the same hashed binaries and offscreen presentation, each
with 30-second idle and activity phases:

| Desktop | Sample operations | Dashboard cycles | Handles at activity start / after collection | Private bytes at activity start / after collection |
| --- | ---: | ---: | ---: | ---: |
| Private | 103 | 15 | 1,508 / 1,972 | 215,392,256 / 241,848,320 |
| Current input | 93 | 14 | 1,312 / 1,637 | 211,853,312 / 236,744,704 |

Cycle totals include four warmup dashboards. Both runs reclaimed every tracked
dashboard window/content and the retired main window, with zero service calls.
The [matched comparison](results/2026-11-03-offscreen-desktop-comparison.json)
records identity, verification counts, phase metrics and binary/report hashes.
The differing operation counts and uncontrolled host load do not support comparing
growth rates. Growth on the input desktop establishes that private-desktop
execution is not required for this offscreen workload. It does not establish
visible-window behavior or distinguish application/native WinUI lifetime from
occlusion/composition resource retention. These probes were working-tree additions
on `7b375a6d`; actual DLL hashes identify the measured code.
Later review tightened thread-local activation checks, exact launcher desktop
matching, failure preservation and default-mode compatibility with older probes.
The recorded comparison predates those diagnostic refinements. Timed offscreen
activity includes presentation checks; it is not comparable with legacy snapshot
CPU/allocation measurements.
Post-review five-second pilots passed on both desktops: six dashboard cycles,
eight shows, 31 presentation verifications, zero service calls and no surviving
tracked windows or content. The runner also completed startup/activity with the
preserved older default-mode probe whose reports omit the new mode flags; the
candidate DLL was restored and hash-verified. The
[guard validation record](results/2026-11-03-offscreen-guard-validation.json)
identifies these short checks separately from sustained measurements.

### Title-bar mechanism controls and shared cleanup

Further private-desktop, nonactivating offscreen controls reproduced the growth
without the dashboard's visual tree. Replacing the actual dashboard content with
Grid still grew handles from 1,364 at activity start to 1,796 after collection
(15 total cycles, 30-second phases). Its detached content, replacement content and
window wrappers were all reclaimed. Plain Window/Grid then isolated the presenter:

| Plain-window control | Total cycles | Handles: activity start / after collection | Section: after warmup / after activity collection | DxgkCompositionObject: same checkpoints |
| --- | ---: | ---: | ---: | ---: |
| Default presenter | 9 | 1,366 / 1,391 | 111 / 112 | 70 / 70 |
| Tool-window presenter, hidden native title bar | 9 | 1,397 / 1,574 | 123 / 198 | 80 / 145 |
| Same tool-window presenter, native title bar retained | 10 | 1,344 / 1,373 | 100 / 101 | 59 / 59 |

These were 15-second idle/activity phases, with four warmup windows included in
each cycle total. Hiding the native title bar suffices to reproduce this resource
signature in a plain window. It does not require `ExtendsContentIntoTitleBar`, a
dashboard control, generated dashboard bindings or its timers/subclass. The
actual dashboard uses `CreateForToolWindow()` and
`SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false)`; it does not enable
title-bar extension.

Ten-second cleanup controls then distinguished returning to visible-title-bar mode
from resetting native state. Every row below has eight total cycles, including four
warmups, and uses the same `B5583879...` assembly:

| Control before permanent Close | GDI: after warmup / after activity collection | Handles: activity start / after collection | Section: after warmup / after activity collection | DxgkCompositionObject: same checkpoints |
| --- | ---: | ---: | ---: | ---: |
| Hidden title bar, ordinary Close | 300 / 424 | 1,509 / 1,699 | 180 / 260 | 136 / 211 |
| Hide, restore `hasTitleBar: true`, Close | 222 / 324 | 1,432 / 1,598 | 143 / 213 | 98 / 163 |
| Hide, `TitleBar.ResetToDefault()`, Close | 160 / 172 | 1,325 / 1,358 | 95 / 103 | 46 / 48 |
| Historical actual-dashboard Hide + Reset control | 154 / 166 | 1,325 / 1,358 | 93 / 101 | 42 / 44 |

Restoring `hasTitleBar: true` did not correct the growth. Hide + Reset sharply
reduced the measured GDI/Section/Dxgk growth. The historical `dashboard-reset`
mechanism control was subsequently removed after shared production cleanup was
adopted; its data remains in the evidence record. The surviving plain-window
controls deliberately keep literal cleanup sequences independent of the production
helper so they continue to distinguish mechanisms.

A separate five-second pilot exercised the actual MainWindow custom-title-bar
cleanup in this order: Hide, `SetTitleBar(null)`, disable extension, Reset, then
Close. It used six total dashboard cycles and 18 activity operations. GDI remained
133 / 133, Section 82 / 82 and DxgkCompositionObject 27 / 27; process handles were
1,295 at activity start and 1,308 after collection. The replaced MainWindow and
its content were reclaimed, and its native window was destroyed. This is short
private-desktop evidence for the pinned Runtime 2.2.0 / WinUI 2.2.1 path, not a
general safety result for other runtime versions.

The shared `WindowTitleBarLifetime` helper is now called by permanent disposal of
both MainWindow and TrayDashboardWindow. It hides the native surface before reset;
the custom MainWindow path also detaches its XAML title bar and disables extension.
Ordinary hide/show remains a separate lifetime. The shared generated-binding
cleanup remains necessary for managed reclamation. No production collection or
forced COM release is added.

A serial baseline/candidate/baseline comparison used 30-second idle and activity
phases with the actual `dashboard` workload:

| Run | Activity operations | Total cycles | GDI: after warmup / after activity collection | Handles: activity start / after collection | Section: after warmup / after activity collection | DxgkCompositionObject: same checkpoints |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Preserved baseline | 101 | 15 | 302 / 583 | 1,509 / 1,946 | 181 / 366 | 137 / 310 |
| Shared-cleanup candidate | 99 | 14 | 109 / 111 | 1,265 / 1,307 | 68 / 69 | 22 / 20 |
| Preserved baseline repeat | 105 | 15 | 308 / 561 | 1,509 / 1,926 | 183 / 355 | 138 / 299 |

All three runs recorded zero service calls, zero surviving dashboard windows or
content, destruction of the tracked native windows, and reclamation of the replaced
main window/content. USER counts were 74 / 73 in each run, with no GUI-query errors.
Cycle totals include four warmups. Operation counts and host load differ, so these
figures establish the large resource-growth correction under this workload rather
than an exact per-window allocation rate or CPU speedup.

The [compact title-bar evidence](results/2026-11-04-titlebar-lifetime.json) retains
all twelve source-summary hashes, child-report hashes, actual assembly hashes,
mode/budgets, phase counters, query errors and lifetime checks. Kernel type indices
are resolved through each child's own queried name map. GDI/USER and native-type
before readings occur after warmup, before idle; activity-start process counters
come later. They are distinct checkpoints and must not be combined into one delta.
Absent historical GUI instrumentation is recorded as unavailable, not zero.

The baseline DLL hash is `B558387928821916B36D923536293EC360A1F587AFEE3C417D46CE16FD0EE565`;
the measured candidate is `E038A2C0B76628736444778584BECDAA1C6C3092575D92ED20422C77C6E68DB0`.
That candidate predates subsequent pending-initialization failure handling,
early-hide/finally refinements and native-before-close regression assertions.
These reports do not claim final full-test success for those later changes;
final sustained validation remains separate.

Subsequent native regressions passed their positive before-close marker assertions.
As a negative control, omitting the dashboard's `WindowTitleBarLifetime.Release`
call made both native regression cases fail. The failed-initialization case also
reproduced the old path that skipped cleanup. The marker records actual native
title-bar state while the window is still valid, rather than inferring reset from
managed reclamation or HWND destruction. Its checked-state flag is emitted only
after the assertions succeed; a failed assertion produces no success report.

The sixth review identified another concrete gap: synchronous `Dispose` or `Cancel`
steps can throw and skip later teardown. A real throwing cancellation callback
reproduced that failure. The guarded component cleanup now continues through that
failure and failed initialization; all 2,650 Tray tests passed after the correction.
The same run exposed a test that read power-source labels partway through a fake
dispatcher turn. It now waits for complete application of a uniquely identified
snapshot, preserving all outcome assertions. Final full-solution and sustained
validation are recorded separately below.

Early hide and the helper's pre-reset hide serve separate ordering guarantees:
permanent disposal hides before awaiting pending initialization, while native reset
also requires the surface hidden immediately before changing title-bar state.
A failed Hide deliberately stops reset, retaining the prerequisite rather than
attempting the later native mutations. These are guarded teardown semantics, not
permission to continue native reset after an uncertain prerequisite.

[Upstream issue #12210](https://github.com/microsoft/microsoft-ui-xaml/issues/12210)
reports native title-bar composition/GDI retention after window destruction and
supports investigating this boundary. It does not establish the exact retaining
native owner in OpenBlade's pinned binaries. Its
[newer Runtime 2.6.4/Rust report](https://github.com/microsoft/microsoft-ui-xaml/issues/12210#issuecomment-6590971810)
also records a reset-related fail-fast with a XAML title bar. Renew the guarded
native lifecycle validation on SDK/runtime upgrades; the pinned local result must
not be generalized to that different version/binding.

Smaller residual growth remains: the candidate's Composition count increased
14 / 24 across ten activity-window cycles, while the earlier plain Window/Grid
control increased 14 / 19 across five activity-window cycles. Both before snapshots
were taken after warmup, so their intervals include idle as well as activity. These
observations do not establish an application-specific owner for the remaining
Composition growth. The candidate's process handles increased 1,265 / 1,307. The
correction therefore does not prove flat total resource use or identify every
remaining native owner.
Microsoft documents [`Window.Close()`](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.window.close?view=windows-app-sdk-2.0)
as destroying the window, and [XAML owns the lifetime of its framework-created visuals](https://learn.microsoft.com/en-us/windows/apps/develop/composition/visual-layer#working-with-winui-xaml).
A bounded primary-source review found no additional supported app-owned cleanup
operation for this ordinary Window path. Public WinUI [shutdown code](https://github.com/microsoft/microsoft-ui-xaml/blob/b1350c702ccef1c020cb19e6cace5ae25b20a895/dxaml/xcp/dxaml/lib/DesktopWindowImpl.cpp#L1702-L1746)
releases its private XAML source and native window, but that source revision is
not verified as the exact implementation in these pinned binaries. The residual
owner and reclamation deadline remain unresolved; this does not justify closing
XAML's internal composition objects from application code.
Multi-hour stability, visible-window behavior and installed-product power/GPU
residency remain unvalidated here. These binaries use a shared .NET runtime
(`SelfContained=false`) and app-local Windows App SDK runtime
(`WindowsAppSDKSelfContained=true`), which are independent deployment choices.

## Final sustained candidate (2026-11-04)

The final code revision `c7306e46995c44d4acbae5f3f159b9ce4c761f9b`, including
callback-failure containment, ran for 600 seconds idle and 600 seconds of paced
activity. The Release Tray assembly hash was
`11688246328E6B2301287C06CC19C58254B0F822D7B66DAB9C36121428832378`.
The private-desktop offscreen workload completed 2,068 accepted samples, each
with exactly one sample-triggered chart rebuild, and 211 dashboard cycles:
four warmups and 207 activity cycles. It recorded 213 nonactivating shows and
851 presentation checks, with zero service calls. All tracked closed dashboard
windows/content and the replaced MainWindow/content were reclaimed, every
tracked native window was destroyed, and both cleanup-failure checks passed.

| Resource | After warmup, before idle | After activity collection |
| --- | ---: | ---: |
| GDI | 139 | 139 |
| USER | 73 | 73 |
| Section | 85 | 86 |
| DxgkCompositionObject | 30 | 30 |
| Composition | 14 | 221 |
| Event | 523 | 947 |

All queries were available without errors. The dominant title-bar GDI and
Section/Dxgk growth stayed suppressed over this longer run. The remaining
Composition increase was 207 across 207 activity windows; Event increased 424.
The short plain-Window control also showed Composition growth, so this does not
identify an application-owned retaining object or prove a framework defect.

Total process handles rose from 1,284 at activity start to 1,931 after collection.
Private bytes rose from 212,668,416 to 316,424,192 at those checkpoints. Total
resource use therefore did not flatten. The managed retained-byte checkpoint
increased from 4,526,168 after warmup to 4,654,736 after activity; rooted history,
fixtures and the growing weak-reference/report lists remain included. Closed
window reclamation does not imply flat native memory.

Disconnected idle used 359.375 ms CPU over 600.081 seconds, with handles falling
from 1,300 to 1,284. This excludes the installed observation pipeline. The one
startup upper bound was 1,507 ms to loaded layout; uncontrolled caches/load and
one startup sample do not establish a startup improvement.

The [sanitized sustained evidence](results/2026-11-04-titlebar-sustained.json)
records the source/version binding, raw report hashes, per-child type-name map,
phase counters, external sampling series, query errors and cleanup checks.
The full-solution validation result is tracked in [PR #874](https://github.com/OpenBladeProject/openblade-core/pull/874).

## Residual native attribution and retirement (2026-11-04)

Further disconnected probes over `53ce825487b6fedacd7cdc56f5868101b4455297`
identify native creation paths for surviving handles. They do not locate the final
retaining reference or provide a supported application cleanup operation. The
[sanitized evidence](results/2026-11-04-native-retirement-attribution.json) binds
each result to raw-report hashes, instrumented assembly hashes, temporary-source
hashes, native module hashes and matching PDB identities. These are investigation
builds over that source, not clean-head product benchmarks. Raw debugger logs,
addresses and local paths remain outside the repository.

A native breakpoint in the pinned app-local `dcompi.dll` observed a successful
`DuplicateHandle` into `SystemVisualTarget`'s handle field. The same final handle
was type Composition, with one OPEN and zero CLOSE records. Its creation stack ran
through `CXamlIslandRoot::InitializeCommon`, `ContentIsland::RuntimeClassInitialize`
and `LocalContentOutput::Initialize`. A separate trace matched two surviving Event
handles: one created by `IslandInputSiteWinRT::RuntimeClassInitialize`, and one
duplicated by `SystemVisualTarget::Partner::SetInputReadyEvent` through
`ExternalTargetImpl<DesktopWindowTarget>::SetInputReadyEvent` during
`LocalContentOutput::Connect`.

Each Event match also had one OPEN and zero CLOSE records. The probe saved syscall
output pointers at entry, read them on successful return, filtered both breakpoints
to the UI thread and checked the complete entry/return pairing. This avoids using
clobbered return registers or correlating a reused handle value. It does not prove
the two Events belong to the same window or reference the same kernel Event.
Earlier Event matches with CLOSE/reuse history were discarded. Both the self-query
and CDB's handle tracing returned empty stacks; live native breakpoints supplied
the stacks instead. The cause of the empty trace stacks remains unknown.

Matched empty-window and dashboard controls then observed delayed retirement:
30 seconds idle, 30 seconds activity, four extra GC/finalizer rounds with
dispatcher pumping opportunities, and ten seconds idle. The historical `drain-N`
stages used delays, without an acknowledged dispatcher callback or composition
flush. Each created 11 activity windows, plus four warmups.
All tracked closed windows/content and native HWNDs were gone.

| Control / checkpoint | Composition | Event | Process handles | Private bytes |
| --- | ---: | ---: | ---: | ---: |
| Pinned empty, after warmup | 14 | 517 | 1,283 | — |
| Pinned empty, after activity collection | 25 | 549 | 1,327 | 221,061,120 |
| Pinned empty, after extra drains and idle | 25 | 549 | 1,327 | 209,235,968 |
| Pinned dashboard, after warmup | 14 | 519 | 1,305 | — |
| Pinned dashboard, after activity collection | 25 | 552 | 1,351 | 234,332,160 |
| Pinned dashboard, after extra drains and idle | 25 | 552 | 1,351 | 222,294,016 |

Composition and Event counts stayed unchanged across all four historical rounds. Private
commit fell, so the full private-byte increase cannot be attributed to retained
native resources. Per-window observations are included in the evidence; those
precede forced collection and are not interchangeable with post-collection counts.
Read-only island enumeration found only the active main island after every child
closure. Enumeration absence does not establish destruction of the closed native
objects. The final reusable probe omits island enumeration entirely.

An isolated build also tested [Windows App SDK 2.6.5](https://github.com/microsoft/WindowsAppSDK/releases/tag/v2.6.5),
using the same temporary drain harness and empty-window workload. Both package
pins were changed only in temporary central metadata and the output was staged
separately. Its Composition count still rose 14 → 25 and stayed 25 across every
drain and the final idle interval. Events rose 514 → 534, then fell to 532 during
idle; private bytes fell from 213,712,896 to 201,224,192. The versions therefore
differ in some observations, but 2.6.5 does not fix the reproduced Composition
retention. Repository package pins remain Runtime 2.2.0 / WinUI 2.2.1.

The retained `--observe-retirement` diagnostic records per-window counts and the
bounded retirement interval, with separate managed-heap measurements. After review,
each final probe round additionally awaits a low-priority dispatcher callback;
this is not a native composition flush. Before the callback addition, a
30-second empty-window control using the cleaned probe, with no island enumeration
or debugger hooks, again increased Composition 14 → 25 across 11 activity windows
and retained 25 through the extra drains and idle. Events fell 555 → 553 during
the final idle; private bytes fell 222,023,680 → 202,330,112. All tracked closed
windows/content and native HWNDs were gone, with zero service calls. The smoke
tests also cover the observation sequence and the unchanged default mode.

The final callback-enabled 30-second control completed 12 activity windows
(16 including warmup), with zero service calls and no tracked closed windows,
content or native HWNDs surviving. Composition rose 14 → 26 and remained 26
after every acknowledged dispatcher turn and the final idle interval. Events
stayed 557 throughout retirement; private bytes fell 222,633,984 → 212,148,224.
This closes the delay-only probe's missing-dispatcher-acknowledgement gap without
claiming a composition flush. The measurement preceded the commit of identical
diagnostic code as `a6f0e54b`; the evidence retains its actual instrumented assembly
hash and base revision rather than relabeling it as a clean-head run.

The diagnostic is developer-only and changes no production lifetime behavior. Its observations are
not proof of indefinite retention or full UI-thread shutdown behavior. No supported
app-owned API has been established for closing WinUI's private visual target;
closing borrowed islands, framework visuals, raw handles or the shared compositor
would violate ownership. Residual native retention remains open. The evidence is
ready for a framework investigation without claiming that an SDK update or more
forced collection fixes it.

Two further Claude Opus 5.5/high reviews covered this investigation: a bounded
findings review and a frozen working-tree review with 21 read-only inspection
calls. Confirmed findings led to acknowledged dispatcher callbacks, an explicit
smoke-test time allowance, report-local instrumentation caveats, reused native/GUI
snapshots and preservation of raw timestamp strings during export. The report now
identifies the latest GC's index/generation/concurrency instead of implying that
its heap data always describe a blocking full collection. Rooted report overhead,
fixed retirement intervals and the fixture's four-window warmup are deliberate
documented controls; they were retained. The review does not establish a safe
production workaround or completion of the native ownership investigation.
The final committed diagnostic code `a6f0e54b` rebuilt and passed all 2,650 Tray
tests, with zero failures or skips, including both real native resource regressions.
Formatting and the older-probe rejection check also passed.

## Native retaining reference and persistent shells, November 4

The [sanitized native reference ledger](results/2026-11-04-native-layoutsync-reference.json)
identifies the remaining Composition reference in the exact measured Microsoft
UI Input and composition binaries. `LocalContentOutput` acquires a
`SystemVisualTarget::LayoutSync` interface. Its disposal path transfers another
interface to deferred cleanup, but leaves LayoutSync in the allocation. The
observed `ContentIsland::Destroy` path frees that allocation without running the
derived cleanup that releases LayoutSync. The target's actual Close runs; its
final reference count remains one, with the same Composition handle still open.
Deletion through a nonvirtual base is a source-level inference; the skipped
cleanup and unmatched reference are the binary observations. This does not
attribute all Event or private-memory growth to the same cause.

Production language changes now keep both native Window shells and their permanent
content hosts. Localized MainWindow and TrayDashboardWindow views are UserControls.
The main shell retains its Mica controller and icon across replacements, while
rebinding the backdrop's theme source. Existing native notification subscriptions
also survive the transaction. A failed attachment restores the old views; retired
views cannot close or hide their successor. No private COM release, borrowed-handle
closure, SDK patch or production GC is used.

This exposed two application retaining references. A notification lambda shared a
compiler-generated closure with a dashboard callback and held the original main
view. It is now a method-group callback that resolves the active view. Native
AppWindow callbacks also retained retired main and dashboard views after ordinary
managed roots were gone. Reacquiring `HostWindow.AppWindow` for removal used a
different managed projection's event-state table. Each view now retains the
projection through which it registered and unregisters through that same instance.
The diagnostic native callback routes and weak-reference results, rather than an
empty ordinary `gcroot` result alone, established this cause.

The [localized-shell comparison](results/2026-11-04-localized-shell-retirement.json)
records the controls and provenance. Keeping native shells alone still added one
Composition handle per language change when Mica was recreated. Reusing Mica held
Composition at 13. Caching AppWindow removed the remaining retired views and held
Timers at 15. Reapplying the invariant icon on every attachment still increased
GUI objects; setting it only when creating the native shell removed that trend.

In the final 71-change run, all 297 retired weak references were collected.
Composition remained 13 and Timers 15. GDI objects were 99 before and after;
USER objects changed from 79 to 77. Events stabilized within 449–452 after the
24-change checkpoint. These are bounded observations of the disconnected,
private-desktop workload. Private memory still increased, so the handle and
view-retirement fixes do not establish complete native-memory stability.

The requested sanitized Claude Opus 5.5/high review was attempted with explicit
approval, but returned the weekly usage limit with zero model tokens and no
findings. It is incomplete. Earlier completed reviews cover earlier changes,
not this persistent-shell refactor. A separate Codex review found no consequential
production regression; execution checks remain independently required.

## Interpretation

Baseline launch-to-loaded-layout upper bounds ranged from 2.33 to 3.70 seconds
(median 2.69 seconds, five processes). These are neither cold-disk startup nor a
paint fence. Baseline disconnected idle used 375 ms CPU over 300.02 seconds,
approximately 0.125% of one logical core, with stable/decreasing private bytes
and handles. Neither figure establishes installed-product startup or idle power.
Candidate startup ranged from 2.39 to 2.73 seconds (median 2.54, five processes).
Candidate idle used 328.125 ms CPU over 300.013 seconds, approximately 0.109% of
one logical core. Uncontrolled host load and differing operation counts prevent
treating these comparisons as a measured startup or CPU speedup.

Repeated replacement stresses language-rebuild lifetime; ordinary tray toggles
reuse a window. Snapshot mode also refreshes the chart on hidden pages, so the
activity workload is deliberately synthetic stress rather than typical page
observation. Clock regressions invalidate the timestamped workload; the probe
does not manufacture future samples to hide a changed clock. Forced collections
occur outside measured phases and only in developer validation.

The regression checks object reclamation, destroyed native windows, empty owned
host tables, accepted samples and zero service calls. It imposes no CPU, timing
or private-memory threshold. Whole-Service/Session Agent consumption, installed
GPU residency, energy, battery power and multi-hour behavior remain separate
measurements; no installation, hardware mutation or live input hook was used.

Six independent Claude Opus 5.5/high read-only reviews covered the findings,
probes and shared fix. Confirmed findings led to shared teardown failure handling,
phase-file sharing, exact per-sample rebuild accounting, destruction-event checks,
bounded concurrent Git-output drains, and preservation of cleanup failures. Style-only
duplication requests were rejected; hidden-page stress and clock assumptions are
documented limits. The third review covered atomically published test-report
sharing; the fourth covered the offscreen controls. Its confirmed findings led to
older-probe compatibility, shared input preparation, thread-local activation
checks, bounded foreground-owner queries, exact desktop matching and stronger
native regression assertions. Fresh presentation admission remains inside the
activity workload, and an input-desktop switch intentionally invalidates the run.
Separate Codex reviews checked the native diagnostic ABI, buffer bounds and the
guard changes. The fifth and sixth reviews covered native title-bar lifetime and
its regressions; their confirmed findings included early-hide ordering, cleanup
after failed initialization and synchronous-failure containment. The sixth review
completed successfully as a review run, with the containment changes and real
callback-failure regression still in progress. This is not a claim that review
found no issues or that all final checks passed.

Other review claims were adjudicated against the actual contract and evidence:
app-local pinned Runtime 2.2.0 prevents arbitrary installed Runtime 2.6.4 drift,
while an intentional SDK/runtime upgrade requires renewed native validation;
Hide failure deliberately stops reset; checked-state output follows successful
assertions; and the omitted-cleanup negative control demonstrates that the native
marker detects missing cleanup. These reviews do not substitute for execution
evidence.

## Native resource-map and graphics attribution (2026-11-04)

The [sanitized attribution ledger](results/2026-11-04-native-resource-map-attribution.json)
separates three remaining native allocation trends. In a standalone public-API
reproduction, repeated `MrmGetChildResourceMap` and `MrmGetResourceCount` calls for
`Resources/MainWindow_ButtonCheckNow` produced 1, 51 and 101 allocated subtrees.
All 101 remained after `MrmDestroyResourceManager`. Completed locked heap walks
counted the returned native type in the pinned `MRM.dll`; this process used no
WinUI, managed resource projection or hardware calls.

A separate 36-change application dump identifies four sampled four-byte ID arrays
as fields at offsets 72/80 of native `ResourceMapSubtree` allocations. Pinned
[Windows App SDK source](https://github.com/microsoft/WindowsAppSDK/blob/2c807cd64d7d51f5cae2294332eea82d90e48246/dev/MRTCore/mrt/mrm/mrmmin/ResourceMap.cpp#L934)
allocates a subtree per lookup, while its
[managed-facing native projection](https://github.com/microsoft/WindowsAppSDK/blob/2c807cd64d7d51f5cae2294332eea82d90e48246/dev/MRTCore/mrt/Microsoft.Windows.ApplicationModel.Resources/src/ResourceMap.h#L21)
assigns ownership to the manager and performs no destruction. XAML's
[property-bag path](https://github.com/microsoft/microsoft-ui-xaml/blob/b229fbd15ceb090a2952985955805650e41390d8/dxaml/xcp/components/mrt/ModernResourceProvider.cpp#L703)
uses subtree lookup and resource counting. This source corroborates the ownership
mismatch; its revisions are not established build provenance of the measured DLL.

The separate 192-change run reclaimed all 781 retired references. Composition
stayed 13 and Timers 15. Events stepped from 414 to 604 by change 36, then stayed
within 604–609 through final idle. Three native Windowing ChangedArgs/adapter
allocation types each grew by two per change: 304 payload bytes per change,
58,368 bytes total. Their cleanup remains unresolved. An earlier 70-change dump
contained no managed event-argument RCW instances; it is a different process and
does not exclude every possible retaining owner.

In the 60-change exact-size run, 131,351-byte allocated blocks increased from 2
to 24 and stayed 24 after idle. A sampled block's common-object function pointers
resolve into `igd10umt64xe.DLL`, supporting an Intel graphics-runtime connection.
This does not establish a leak, pool bound, reclamation policy or Vulkan path.
Both allocation-stack attempts failed, first with debugger syntax and then a
120-second timeout; no valid direct allocation stack was captured.

Native memory is not fully resolved: private bytes in the 192-change run rose
from 174,538,752 after warmup to 513,323,008 after idle. Allocated heap storage,
free allocator capacity and total private memory are different measurements.
These dirty investigation builds used base `7f457076`; their exact binary and
raw-artifact hashes are in the ledger. Temporary instrumentation was removed and
pre-instrumentation backups restored. No private native free or custom ABI shim
was shipped. The requested sanitized Claude review remains incomplete after the
weekly quota failure, with zero model tokens and no findings.

## Windowing event-argument mitigation (2026-11-04)

A [subscription-removal control](results/2026-11-04-windowing-change-subscription-control.json)
isolated the remaining native ChangedArgs families. Omitting only the main
window's `AppWindow.Changed` registration removed all three sampled types at
warmup, 12 and 24 language changes, and final idle. The matched earlier samples
contained 12, 36 and 60 objects of each type. This eliminates the observed
304-byte payload increase per language change; it does not establish that no
event arguments are transiently allocated. The first attempted control had no
heap instrumentation in its runner and is excluded.

The production fix moves size enforcement into a shell-owned native subclass
and uses the pinned SDK's public `OverlappedPresenter` minimum/maximum properties.
Actual projection metadata contains these APIs even though the selected XML
reference omitted them. The newer XAML `Window.MinWidth` properties are absent
from the pinned WinUI projection. Microsoft documents presenter limits for both
[user and programmatic resizing](https://learn.microsoft.com/en-us/windows/apps/develop/ui/manage-app-windows#overlapped-presenter).
The helper retains the existing 800-by-600 DIP outer-window policy, clamps it to
the work area, updates on native DPI/display changes, and survives localized
content replacement. The DPI transition guard covers clearing, default message
handling and reapplication, including a failed clear. Maximized bounds can
include [invisible resize borders](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getwindowrect#remarks);
explicit reconciliation applies only to restored windows.

The [fix census](results/2026-11-04-window-sizing-native-fix.json) completed 58
language changes and reclaimed all 245 tracked retired references. All three
ChangedArgs families remained absent at six checkpoints, including idle.
Composition stayed 13 and Timers 15; GDI stayed 89, USER changed 80 to 77, and
Events ended at 448 after starting at 417. Every locked heap walk completed,
unlocked successfully and included the Windowing module. These are dirty
diagnostic builds based on `645bf03e`, with actual binary and input hashes in the
ledger. Temporary census code is excluded from the delivered product.

Private bytes still increased from 176,136,192 to 388,198,400. The independent MRM
ownership leak and Intel-linked allocation investigation remain unresolved;
this fix does not establish overall native-memory stability. The new native
sizing smoke covers hidden and visible resizing, available displays, changed
constraints while minimized, maximize/restore, a same-DPI undersized suggested
rectangle, range replacement and native callback destruction. Tests use an
isolated desktop and do not change monitor configuration or installed services.

After removing instrumentation, the full solution build passed with zero warnings
or errors and all 11,378 tests across 17 projects passed, with no failures or
skips. The separate Release Tray suite passed all 2,660 tests, including direct
Win32 resizing. Fresh English and pseudo-localized Main-window renders were
inspected. Independent Codex review found no remaining actionable issue in this
fix; the requested Claude review remains incomplete because of its weekly quota.

## Close-to-hide event retention (2026-11-04)

A separate [matched close/hide control](results/2026-11-04-window-closing-subscription-control.json)
found retention in `AppWindow.Closing`. Both fresh processes used one bare window,
the same event subscription and three canceled closes during warmup. After 200
additional close requests, three native ClosingArgs/adapter groups each grew
from 3 to 203 and remained after collection and idle. Direct hiding left all
three counts at 3. Matching cached PDB symbols identify the types; their combined
payload is 128 bytes per canceled close, excluding allocator overhead.

Main and Dashboard now share a native `WM_CLOSE` interceptor, preserving their
existing hide policies before WinUI creates these arguments. Each attached view
owns its callback; detach and native destruction release it. Normal terminal
closes still reach Windows, and callback failures remain inside the native
boundary. The [fix census](results/2026-11-04-window-closing-native-fix.json) used
this production helper for the same 200-cycle workload: all three groups stayed
absent at warmup, 50, 100 and 200 cycles, and idle. All five heap walks completed,
unlocked and included Windowing coverage without hash-table overflow.

The first attempted baseline controller supplied `ArgumentList` to a custom
launcher that only forwarded `Arguments`. It produced no diagnostic reports and
is excluded. The launcher now rejects nonempty `ArgumentList` before creating a
process, using reflection to retain Windows PowerShell/.NET Framework support.
Its five focused tests and Windows PowerShell compilation passed. A private
desktop does not isolate named-event IPC from an installed instance.

The new native smoke verifies repeated and system close commands, duplicate
registration, handler replacement, callback failure, allowed close and explicit
terminal `Window.Close`. The focused localization-retirement test also passed
with the integrated Main/Dashboard handlers. Temporary instrumentation is
excluded from delivered sources. These results address the measured ClosingArgs
groups; the separate MRM ownership leak and Intel-linked allocation investigation
remain unresolved. The requested Claude review remains incomplete after the
weekly quota failure; independent Codex review and runtime checks are reported
separately.

After removing temporary instrumentation, the full solution build passed with
zero warnings or errors and all 11,381 tests across 17 projects passed, with no
failures or skips. The clean Release developer harness was rebuilt and checked
for absence of the temporary census markers. Independent Codex review found no
actionable issue in the integrated close-handler change.
