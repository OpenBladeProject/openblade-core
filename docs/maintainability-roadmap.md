# Autonomous maintainability roadmap

Tracking issue: [#532](https://github.com/OSSBlade/openblade-core/issues/532).
Baseline: `e179191292329eed7257a7d8736ceb529ac327f2` (2026-09-20).

## Goal and boundaries

Make recurring changes to OpenBlade easier to reason about by giving each
cohesive state machine or policy one owner, small explicit inputs, and tests
that explain its contracts. Reduce the number of unrelated responsibilities a
maintainer must understand for one change. Preserve observable behavior unless
a separately tracked, reproduced defect requires correction.

The previous maintainability series is complete, including the publication
comparer and fan editor extractions in #528/#529 and the eligibility correction
in #531. This roadmap defines a new, bounded improvement cycle. File size is a
hotspot signal, not a completion target. Do not split files mechanically, move
complexity behind callback facades, introduce duplicate mutable state, or build
speculative frameworks merely to reduce line counts.

Follow [AGENTS.md](../AGENTS.md) and [VERSIONING.md](../VERSIONING.md). Keep the
service's exact-device admission, live safety checks, hardware serialization,
write authority and persistence chronology in their authoritative owner. Keep
UI-affine awaits and notification ordering intact. This roadmap authorizes no
installation, release publication, live hardware writes, or unattended sleep.

## Delivery queue

Execute milestones in order. Independent analysis can run in parallel, but
merge each change against current main before validating the next integration.
Create a separate labeled issue for each implementation before editing.

### M1: lighting editor state

Target: `src/OpenBlade.Tray.Core/MainViewModel.cs`, using the completed fan
editor extraction as a reference rather than a required generic abstraction.

- Map current lighting load, effect defaults and decoding, imported sequences,
  draft conversion, dirty tracking and power-source refresh paths. Record
  invariants and existing test coverage before choosing the exact boundary.
- Extract a cohesive UI-independent lighting editor owner. Keep service calls,
  commands, admission, async orchestration and binding notifications in the
  view model. Avoid a second copy of the editor's mutable state.
- Preserve effect parameters and logo mode in load-to-draft round trips.
  Equivalent loads must neither dirty the editor nor emit redundant bound-value
  notifications. Failed commands preserve drafts and dirty state; Applied or
  Stored clears dirty only if the current draft still equals the submitted
  value. A newer accepted status reloads only when it reflects that request.
- Confirmed power-source changes discard the old-source draft; transient
  Unknown does not. Preserve unconfirmed PID 02E0 effects where zero brightness
  means unknown, using a visible default only when appropriate. Imported
  Sequence remains available and sorted without duplicate options. Keep
  lighting power-policy fields outside the keyboard/logo editor unless a
  separate ownership analysis justifies their inclusion.
- Add focused tests for those boundaries and retain integration tests through
  MainViewModel. Validate any affected bindings and rendered states when the
  change touches UI behavior; a pure extraction must show why visual behavior
  and XAML remain unchanged.

Done when one owner implements editor transformations, view-model wrappers
only coordinate/present them, focused and integrated checks pass, independent
review is resolved, and the PR is merged and cleaned up. Do not force all
lighting orchestration into this component if it would cross ownership lines.

### M2: tray-dashboard customization editor

Target: `TrayDashboardPreference` editing in
`src/OpenBlade.Tray/MainWindow.xaml.cs`: loading, option availability, visible
ordering, add/remove limits and preference conversion. Main Dashboard cards and
generic page-section customization are separate systems and outside this slice.

- Extract editor state and transitions while keeping WinUI controls, window
  lifetime, persistence, notifications and observation wakeups in MainWindow.
- Reuse `TrayDashboardPreferenceDefaults` normalization; do not establish a
  second competing implementation or change persisted preferences.
- Cover load/edit/save round trips, ordering, duplicate/unknown entries,
  availability changes, visibility limits and unchanged edits. Retain window
  integration coverage. Build Tray and inspect rendered customization states
  when interactive access is available; otherwise record the limitation and
  retain focused state tests plus XAML wiring contracts.
- Update source-contract tests that currently inspect implementation strings
  only in MainWindow.xaml.cs. Preserve XAML/wiring assertions; test extracted
  editor transitions behaviorally rather than retaining misplaced production
  logic merely to satisfy file-location assertions.

Done when window event handlers delegate cohesive transitions, existing
preferences and user-visible order are preserved, review and relevant tests
pass, and the PR is merged and cleaned up.

### M3: reassess service and test maintainability

After M1/M2, examine current main and recent change history. Produce a short
issue comment with ranked candidates, evidence, proposed owner and expected
maintenance benefit. Consider exact-fan ownership bookkeeping, lighting editor
follow-up gaps, and large test fixtures only where coupling causes a concrete
maintenance problem. Do not move live admission across awaits into a cached
permission object. Do not split tests only because the test file is large.

If exact-fan ownership qualifies, bound it to pure ownership transitions around
ExactFanControlOwnership and observed-topology classification. Keep epochs,
live controller/admission reads, target clearing, submission chronology and
writes in the runtime. Cover Unowned, ManualTopologyOnly, OwnedExactIntent and
SubmissionUnresolved directly, replacing private-method reflection coverage
where appropriate. Deferred manual-fan recovery is outside that slice.

A candidate is eligible only when all are true:

1. A specific recurring edit, duplicated invariant, review difficulty or
   independently reproduced defect demonstrates the problem.
2. A cohesive owner can be named without duplicating authority or changing
   behavior, IPC, schemas or hardware admission as an incidental side effect.
3. Focused failure, transition and integration tests can prove the boundary.
4. Benefit justifies migration and review cost; a small local simplification
   would not solve the same problem more clearly.

Implement at most two eligible candidates in this cycle, as separate issues
and PRs. If none qualifies, record the evidence and complete the milestone with
no extraction. Broader redesign, product behavior changes and compatibility
changes require a separate proposed scope; do not silently expand this cycle.

### M4: integrated completion audit

Verify every milestone against current source, issue/PR state and exact final
validation artifacts. Close the tracker only after required PRs are merged,
main is synchronized, reproduced findings are resolved, worktrees/branches are
cleaned up, and the audit distinguishes completed work from optional future
ideas. Retain local evidence outside worktrees before deleting them.

## Autonomous working loop

1. Read this roadmap, the tracker and current repository instructions. Inspect
   main, open PRs, dirty worktrees and live worker/process handles. Resume the
   recorded active slice; do not restart an operation just because a polling
   timeout expired. Preserve unrelated work.
2. Select the first incomplete milestone. Write its issue with the exact owner,
   preserved invariants, non-goals, tests and acceptance criteria. Record the
   issue/branch/head and active validation handles in the tracker or task record.
3. Work in a dedicated main-based worktree. Start with up to two workers only
   for independent bounded scopes; one owner integrates shared-file changes.
   Use available models according to the repository delegation policy. Existing
   permission to fall back when Spark is unavailable applies; disclose it.
4. Implement one coherent slice. Independently reproduce review findings before
   fixing them. Track behavior fixes separately with required version/changelog
   changes; internal refactors normally have version impact none.
5. Obtain independent read-only review for each substantive refactor. Use the
   installed Claude delegator when available, within its skill constraints.
   If unavailable before starting, record that and use an independent Codex
   reviewer; never label that review Claude. Do not blindly retry a started
   Claude invocation. Verify actionable claims against source and focused tests.
6. Run focused checks first, then required repository/full-solution and selected
   CI lanes at the final commit. Capture source/base SHA, commands, exit codes,
   counts and limitations. Inspect UI when applicable. Do not equate tests with
   proof of unchanged ownership or UI synchronization; review those explicitly.
7. Refresh from main and recalculate version impact before readiness/merge.
   Revalidate changes introduced by integration. Existing authorization permits
   future merges; no routine confirmation is needed once gates pass. Match the
   merge to the validated head. If automatic approval rejects an action, follow
   that decision and obtain the requested authorization; do not work around it.
8. Complete merge cleanup and synchronize the primary checkout as AGENTS.md
   requires. Update the tracker with evidence and the next concrete slice.
   Continue through the bounded queue without asking what to do next.

Hosted CI's known billing-startup failure may use the previously authorized
local equivalent only after confirming that failure on the current run. Record
the missing hosted execution and the matching local scope. Actual test failures,
unexplained CI errors and incomplete required validation are not bypasses.

Ask for input only for a material scope/product decision, incompatible contract
change, required interactive/hardware action, unavailable necessary review or
validation without an authorized alternative, or a tool approval rejection.
A blocked slice does not authorize extra speculative work merely to stay busy.

## Progress and stopping rules

For each milestone, report the responsibilities and duplicated invariants
removed, remaining dependencies, focused tests, reviewed risks and integration
result. Track line counts only as context. A smaller coordinator with the same
hidden coupling is not success; new components must be used by production paths
and establish an understandable ownership boundary.

Completion means M1/M2 are delivered (or a source-backed revised design is
explicitly accepted), M3 is resolved under its evidence gate, and M4 passes.
Remaining optional ideas must not silently extend this goal. Reopen a new cycle
only when later feature work, defects or measured review friction justify it.
No recurring job or unattended schedule is created by this document. Execution
is through the active task/goal; retain a concrete checkpoint before yielding.
