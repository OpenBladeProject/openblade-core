# Native resource validation, 2026-11-03

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

## Remaining native-resource growth

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
ran on inactive private desktops. Attribution to application content, WinUI or
composition remains unresolved. No forced native release, production
delay or package upgrade was introduced on this evidence.

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

Four independent Claude Opus 5.5/high read-only reviews covered the findings,
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
final guard changes. These reviews do not substitute for execution evidence.
