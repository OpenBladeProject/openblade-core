# OpenBlade agent guide

This file is the first instruction source for AI coding agents working in this
repository. Keep changes narrow, evidence-based, and suitable for a native
Windows hardware utility.

## Parallel work

Use parallel subagents when a task has multiple independent workstreams and the
expected speed or quality gain justifies the added token and coordination cost.
Prefer delegation for bounded, read-heavy work such as codebase exploration,
reviews, test or log analysis, triage, and research. Give each subagent a clear,
non-overlapping scope, wait for all requested results, then synthesize and
verify them in the main task.

Keep dependent work and overlapping code edits with one owner. Do not delegate
trivial tasks or split write-heavy work when merge conflicts and coordination
would outweigh the benefit. When parallel editing is useful, assign disjoint
files or components and inspect the combined diff before verification.

## Proportionate execution and authorization reuse

Use the smallest workflow that resolves a concrete uncertainty and completes
required verification and reviews. Before adding a check, capture, study, or
validation step, identify what remains uncertain and how its result will affect
the next action. Reuse conclusive evidence while the relevant source, device,
and conditions remain unchanged; repeat a check only when a change, failure,
or unresolved concern justifies it. Exact-head and fresh per-write safety
checks remain required where specified below.

Reuse explicit user authorization within its stated scope throughout the task.
Do not ask again for the same authorized action merely because execution moves
to another step or agent. Ask when the next action exceeds that scope or an
existing rule requires fresh approval, including attended sleep/resume testing.
This policy grants no hardware authority and waives no required review,
approval, or safety requirement.

For authorized hardware validation, prefer one captured, typed, reversible test
that resolves the uncertainty: read and save the confirmed baseline, pass fresh
exact-device and applicable safety admission, apply one value, confirm it by
readback, restore the exact saved state, and verify restoration by readback.
Add further tests only for a concrete remaining uncertainty or required gate.
If submission or readback is ambiguous, stop further writes and reconcile
read-only. If restoration fails or cannot be confirmed, stop and report the
unresolved state; never replay an uncertain write, guess, or widen authority.

## Product boundary

OpenBlade is a lightweight Windows utility for Razer Blade laptops. The
architecture can admit more Blade models, but a hardware capability is enabled
only for an exact model and SKU backed by sanitized, reviewed capture evidence.
Captured BIOS versions are provenance by default; a capability may impose an
exact firmware restriction only through an explicit reviewed admission rule,
and no shipped capability imposes one today.
Never copy a command from another model, expose raw firmware commands, or
weaken exact-device checks to make a feature appear supported.

Production support is exact-device scoped. Current admitted targets include the
Razer Blade 16 RZ09-0581 (`1532:02E0`) and RZ09-0528 (`1532:02C6`); neither
model's device-specific assumptions may be copied to the other.

## Codebase map

- `src/OpenBlade.Contracts`: shared service IPC, profiles, capabilities, and
  typed device state. Keep it platform-light and backward-aware.
- `src/OpenBlade.Protocol`: 90-byte Razer report encoding, validated command
  fixtures, checksums, transaction matching, and transports.
- `src/OpenBlade.Service`: LocalSystem device ownership, automation, conflict
  handling, telemetry, configuration, and named-pipe server.
- `src/OpenBlade.Tray.Core`: UI-independent presentation logic, service client,
  per-user preferences, and reusable bounded pumps.
- `src/OpenBlade.Tray`: WinUI 3 window, tray icon, Mica shell, controls, and
  current-user integrations.
- `src/OpenBlade.Session`: contracts for current-user Session Agent IPC.
- `src/OpenBlade.SessionAgent`: always-running current-user display, input, and
  host-rendered lighting work that cannot run correctly as LocalSystem.
- `src/OpenBlade.Display`: Windows display enumeration and mutation.
- `src/OpenBlade.Capture`: developer-only, typed protocol inspection and
  reversible validation commands. It is never part of the installed product.
- `src/OpenBlade.FirmwareSmoke`: developer-only installed-product validation
  before and after laptop firmware updates. It uses typed service IPC, requires
  a read-only green gate before reversible writes, and always restores state.
- `src/OpenBlade.Setup`: transactional install, repair, shutdown, and uninstall.
- `tests/*`: project-aligned tests plus `Build.Tests` release-payload checks.
- `captures`: submodule containing sanitized evidence and capture tooling.
- `build`: publish, package, and deployment scripts.

## Architecture rules

1. The service is the sole owner of the laptop control HID handle. It exposes
   typed operations only and serializes all hardware access.
   It also owns keyboard Driver mode continuously while the laptop is fully
   active: it performs verified read-before-write maintenance and is the only
   component allowed to change that mode. At a confirmed low-power boundary it
   may restore Normal mode only after the existing identity and
   competing-controller safety checks pass, and it must not reacquire Driver
   mode until Windows reports that the system is active again. The Session
   Agent consumes confirmed status only and must pass function-row keys through
   whenever Driver-mode status is unknown. During coordinated shutdown, the
   service uses the same guarded Normal-mode release before bounded fan cleanup
   so a fan failure cannot skip keyboard cleanup. It hands OpenBlade-owned
   manual fan control back to firmware Automatic only after confirmed-state,
   exact-device, and competing-controller safety checks pass, without changing
   the saved fan intent. A first observation or matching desired state does not
   establish fan ownership; only a successful service fan write does.
   Production lighting admission is owned by the approved embedded oracle and
   the reviewed semantic admission manifest. The manifest compares the actual
   device identity, command contracts, report geometry, layout mask, supported
   values, and a known-answer encoded report; unrelated source edits do not
   require re-approval. A machine-local
   exact-build validation artifact is developer evidence only and must never
   independently enable production writes.
2. The Session Agent owns current-user APIs and input observation. The visible
   tray may exit without disabling profile automation, display automation,
   interactive lighting, or configured special keys.
3. UI-only work must not rebuild, replace, or advance the installed service or
   Session Agent. Use the tray-only deployment path only when shared Contracts
   and both IPC versions are unchanged, and verify protected hashes. Any shared
   contract or IPC change requires a coordinated full install.
4. A temporary read, pipe, or enumeration failure is not proof that the laptop
   disappeared. Preserve last confirmed presentation through bounded retries,
   but perform fresh identity and safety checks before every write.
5. Never stop competing Razer software. Strict thermal control remains
   advisory for externally changed performance and fan state. Maintain desired
   state may reconcile confirmed thermal drift only after fresh competing-
   controller absence, exact-device, power, session, durable-budget, and
   readback admission; ambiguity or possible submission pauses instead of
   fighting or replaying a command. Only the user-enabled lighting ownership
   policy may restore lighting automatically.
6. Keep queues bounded, cancellations observable, writes atomic, and native
   handles deterministically released. Do not add a kernel driver or depend on
   Synapse components. As an optional integration, the native-brightness path
   may use each admitted model's own exact, signed Razer driver stack (`02E0`
   for RZ09-0581, `02C6` for RZ09-0528) when that stack is already installed
   or separately provisioned. Keep WMI fallback available, never mix one
   model's stack with another's, and never generalize a stack to an unadmitted
   model or driver build. Developer export tooling does not authorize installer
   bundling or publication without the corresponding redistribution
   authorization, notice, hashes, and signature validation.
7. Prefer one shared implementation or presentation primitive over
   page-specific fixes. When a bug can affect every picker, card, or firmware
   field, fix the shared layer and add a regression test there.
8. Do not add abstraction layers, background loops, or broad test matrices
   without a concrete current requirement. Favor the smallest design that
   fully implements the requested behavior and remains understandable.
9. Never silently drop a feature discovered on a different model. Represent it
   in the shared capability contract, diagnostics, model documentation, and
   gated UI even when the only honest access state is `ObservedOnly`. A missing
   backend or readback blocks mutation; it does not justify hiding the model
   difference or substituting another model's assumptions.

### Tray async context

`OpenBlade.Tray.Core` deliberately uses two async-context modes. Command paths
capture the WinUI context: the bare `await Task.Yield()` in
`AsyncRelayCommand.RunAsync` is the mechanism, and `SetPerformanceAsync`,
`SetCustomPerformanceAsync`, `ApplyFanControlAsync`, `SetLightingAsync`,
`SetChargeLimitAsync`, `ToggleBatteryProtectionAsync`, and
`ToggleFullChargeAsync` mutate bound state in a `finally` after an await.
`UpdateCheckViewModel.RunAutomaticCheckAsync` and `CheckCoreAsync` also capture
the UI context because they mutate bound update status after awaiting the
release client. `FirmwareUpdateAssistantViewModel.RunAutomaticCheckAsync` and
`CheckAsync` capture it for the same reason after awaiting the Razer firmware
client.
`TrayCompleteShutdownCoordinator.ShutdownAsync` also captures it because its
Session Agent signaling and tray-exit callbacks are UI-affine.
`App.RunElevatedServiceControlHelperAsync` captures it after off-dispatcher
service work because `Exit()` must run on the WinUI thread.
Background pumps, loops, queues, and `ServiceClient` use
`ConfigureAwait(false)`.

xunit runs without a `SynchronizationContext`, so tests cannot distinguish
these modes or detect a wrong annotation in a UI-affine path. Do not enable
CA2007 or bulk-add `ConfigureAwait(false)` in Tray.Core.

## Hardware and capture safety

- Treat all uncaptured write commands as unsafe and unsupported.
- Keep validation proportionate. When the vendor software already offers a
  transition and OpenBlade would send only captured commands, test it directly
  through OpenBlade's typed, reversible path instead of first requiring new
  vendor captures, annotations, or lifecycle studies. Add evidence steps only
  for a concrete risk, such as an uncaptured command, an unrecoverable state,
  or an unexplained readback.
- Read/query first. For a new write, save prior confirmed state, show the exact
  action, apply one value, read it back, and restore it.
- Explicit approval for unattended hardware validation extends only to
  captured, typed, reversible operations. Before each such operation, read and
  save the prior confirmed state, pass fresh exact-device and applicable safety
  admission, confirm the result by readback, and restore the saved state. If
  submission or readback becomes ambiguous, stop further writes and reconcile
  read-only. If restoration fails, stop and report it; never widen write
  authority or guess.
- Never initiate or arm sleep/resume validation unattended, including while
  the user is unavailable. Keep it as the last physical-validation step and
  require fresh approval immediately beforehand from a present user who can
  resume the system and report the outcome.
- Scope physical validation to the behavior and safety conditions exercised.
  When source inspection confirms that different power connectors use the same
  transition handler and relevant state checks, one attended test may validate
  that shared path. Record the connector used and any source-specific behavior
  left untested; do not repeat a disruptive lifecycle test solely to match the
  connector in the original report.
- Never run USBPcap capture and OpenBlade control against the same interface at
  the same time.
- Never call `GenerateConsoleCtrlEvent` with process group `0`. USBPcapCMD must
  run in its own process group and receive a targeted `CTRL_BREAK_EVENT`; force
  termination is an explicit fallback that may lose buffered capture data.
- Never commit raw PCAP files, device serials, user paths, or unsanitized
  payloads. Capture provenance and restoration results belong in the sanitized
  annotation.
- Preserve untracked `.capture-control-*` and `.query-control-*` directories
  unless their owner explicitly asks to remove them.

### Captures history boundary

The `captures` submodule history was rewritten; super-repo commit `568fa88` is
the boundary. Commits before it reference submodule commits absent from the
remote, so `git submodule update`, `git bisect` across the boundary, and
historical CI re-runs fail by design, not by fault. An existing local clone may
still resolve the old pointers from unreferenced objects, while a fresh clone
will not; do not conclude from local success that CI is misconfigured. To
investigate older super-repo commits, check them out without recursing
submodules.

## Change workflow

All work that may modify repository files must use a dedicated Git worktree for
the task. Do not implement changes, run change-producing builds or tests, stage,
or commit from the primary landing checkout, even when it is clean. Read-only
inspection may use the landing checkout. Create the worktree from `main` by
default unless the user explicitly requests another base, and preserve all
unrelated worktrees and checkout changes.

1. Inspect `git status`, the affected contracts, and existing tests before
   editing. User changes and unrelated dirty files are not cleanup targets.
2. Whenever creating an issue, apply the relevant repository labels.
3. For every fix, create the tracking issue before implementing the change,
   then deliver the implementation through a pull request.
4. Update the smallest authoritative layer. Do not duplicate state between the
   service, Session Agent, and tray.
5. Add focused tests for parsing, normalization, failure handling, and shared UI
   contracts. Run the affected project tests before the full solution.
6. For WinUI changes, build `OpenBlade.Tray` and inspect the rendered app when
   interactive access is available. XAML contract tests do not prove visual
   quality.
7. For hardware changes, complete the relevant physical validation before
   marking support production-ready, following Hardware and capture safety.
8. Update `CHANGELOG.md` under `Unreleased` for user-visible changes. Every
   new or materially changed changelog bullet must include a Markdown link to
   its implementing pull request, using
   `([#123](https://github.com/OSSBlade/openblade-core/pull/123))`. Reference
   pull requests, not tracking issues; include each implementing PR when a
   bullet combines changes. If the PR number is not yet available, open the
   draft PR first, then add the verified reference before marking it ready or
   merging. Preserve existing references and published release snapshots;
   reference-only corrections to `Unreleased` must link the original PR.
   The Nightly workflow treats each published tag as a release boundary, attaches
   an exact-source changelog snapshot, and opens or updates a draft pull request
   that moves included entries into dated `## [X.Y.Z-nightly.N] - YYYY-MM-DD`
   sections while retaining post-build entries under `Unreleased`. Review that
   generated diff; do not hand-edit the workflow-owned branch or bypass its
   exact-tag and force-with-lease guards. Keep entries as `- ` bullets under
   `### ` categories; continuation paragraphs, nested lists, and fenced blocks
   remain part of the preceding bullet. Editing a bullet already included in a
   published nightly creates a new entry at the next nightly boundary while
   the earlier section retains its exact published wording. Publishing a
   release by hand skips that job, so record the published section in the same
   change: regenerate with `Write-OpenBladeNightlyChangelogSnapshot` from
   `build/NightlyChangelog.ps1` and confirm with
   `build/Validate-ChangelogReleases.ps1`, which fails when a published release
   has no dated section or no comparison link. Nightly preparation now leaves
   a draft: review and merge the generated changelog PR before dispatching
   `finalize-nightly.yml` on `main` with its exact tag. Both hosted and local
   publication must use `build/Complete-NightlyRelease.ps1`, which validates
   live release history and the source snapshot against current `main` before
   making the draft public. A draft PR alone does not complete the changelog.
9. Commit and push coherent slices. Do not bundle capture evidence, product
   code, history maintenance, and unrelated formatting in one commit.
10. Preserve the line endings declared by `.editorconfig` and `.gitattributes`.
   Patch application can insert LF-only lines into an existing CRLF file, and
   `git diff --check` does not detect that mixed state. Before committing,
   inspect every touched text file for mixed endings and normalize the complete
   file to its declared convention. Treat Git's `LF will be replaced by CRLF`
   warning as unfinished work, not as a harmless staging detail.
11. Prefer the authenticated GitHub CLI (`gh`) over application connectors for
    GitHub reads and writes. Use a connector only when `gh` cannot perform the
    needed operation. If `gh` is available but genuinely unauthenticated, ask
    the user before falling back to a connector.
12. On managed hosts, do not treat a sandboxed `gh auth status` failure as
    proof that credentials expired when the same output includes config or
    keyring access-denied warnings. Retry the exact read with approved host
    access; ask for reauthentication only if that host-level check also fails.
13. After a pull request is merged or closed, complete its branch cleanup:
    remove any dedicated worktree, then delete the local and remote pull-request
    branch. First verify the pull request's final state and exact branch, preserve
    dirty or untracked worktree artifacts, and ensure no other live worktree uses
    the branch. For a merged pull request, also prove that the branch tip is
    reachable from `origin/main`. If any safety check fails, report the blocker
    instead of silently leaving cleanup incomplete or deleting uncertain work.
    A worktree that initialized `captures` needs submodule-aware removal. First
    ensure no long-lived process uses the worktree as its current directory and
    prove both the parent worktree and `captures` are clean. Then run
    `git -C <worktree> submodule deinit -f -- captures`, followed by
    `git worktree remove --force <worktree>`; plain removal still rejects a
    worktree that contained a submodule. If Git unregisters the worktree but
    Windows leaves an empty directory locked, do not delete the branch yet:
    identify and restart only the verified locking process from its intended
    working directory, prove the residual directory is empty and unregistered,
    then remove that exact directory.
    After any successful branch merge, also update the primary non-linked-
    worktree checkout to the latest `main` with `git pull --ff-only origin main`;
    this synchronization is the required exception to the landing-checkout
    read-only rule above. Confirm first that the pull will preserve local
    changes. If local modifications or divergence prevent a safe fast-forward,
    report the blocker rather than stashing, resetting, or discarding work.

## README and documentation language

Use the concise [README](README.md), approved in
[PR #558](https://github.com/OSSBlade/openblade-core/pull/558), as the writing
reference for user-facing documentation.

- Write for laptop owners in plain, direct English. Prefer familiar words,
  concrete actions, and short sentences over implementation jargon.
- Keep the README a brief introduction: purpose, supported laptops and Windows
  requirements, main features, installation, and links to detailed guides.
  The approved version is about 440 words; use that as a guide to brevity, not
  a rigid word limit. Put substantial new detail in a linked guide.
- Avoid lawyer-like disclaimers, defensive caveats, repeated warnings,
  promotional claims, and absolute promises about support or reliability.
- State a limitation once, near the relevant feature or step, when it helps
  the reader decide or act. Keep admission rules, compatibility checks,
  failure handling, and validation history in the relevant technical guide.
- Preserve real safety requirements and model-specific limits. Simplify the
  wording without weakening the requirement or overstating support. Distinguish
  tested firmware provenance from actual production admission restrictions.
- Prefer compact feature bullets to many small sections. Avoid repetitive
  bold-label lists, filler, and redundant explanations of what the app does
  not do.
- Write instructions positively and concretely, for example: "Start it
  normally; setup asks for administrator permission when needed."
- Review documentation edits for factual accuracy, length, repetition, and
  tone. Verify changed behavior claims against the implementation. Detailed
  engineering guides should retain the precision their readers need.

## Versioning and commits

Follow [VERSIONING.md](VERSIONING.md). The short form is:

- Name branches for the work, not the agent or vendor. Use purpose prefixes
  such as `feat/`, `fix/`, `docs/`, `chore/`, or `refactor/`; do not use
  prefixes such as `codex/` or `claude/`.
- `Directory.Build.props` is the product-version source of truth.
- Every pull request must complete the `Version impact`, exact `Version change`,
  and `Reason` fields defined in `VERSIONING.md`. Apply any required bump to
  `OpenBladeVersion` in that same pull request, then refresh from `main` and
  recalculate before marking it ready or merging.
- Profile/config schema and IPC protocol versions are independent integers with
  explicit compatibility rules and migrations.
- Use Conventional Commits:
  `type(optional-scope): imperative summary`.
- Typical subjects are `feat(tray): add special-key bindings`,
  `fix(service): retain device state during reconnect`, and
  `chore(capture): record restored fan validation`.
- Do not capitalize the first word after the colon or end the subject with a
  period. Keep it at or below 72 characters when practical.

## Verification baseline

For a normal product change:

```powershell
dotnet build OpenBlade.slnx -p:NuGetAudit=false --no-restore
dotnet test OpenBlade.slnx -p:NuGetAudit=false --no-build --no-restore
```

On managed hosts, run the full solution test command above with approved host
access from the outset. The sandbox consistently denies the loopback client in
`NamedPipeControlServerTests.ServeOnceAsync_ReadsRequestBeforeResolvingWindowsClientContext`
with `UnauthorizedAccessException`; this is a sandbox permission failure, not a
test regression. Focused test projects that do not exercise Windows named-pipe
client identity may still run in the sandbox.

Use `build/Publish.ps1` and the payload validation tests for release or setup
changes. An explicitly approved unattended installation may use only the
enrolled fixed-operation local-development deployment broker, and only when
the approved operation can complete without displaying or requiring an
interactive UAC prompt. While the user is unavailable, never initiate or wait
for UAC, invoke generic elevation, use the broker for generic command
execution, bypass its fixed-operation authorization, or fall back to another
installation path.

Do not run production publishing from a OneDrive-linked worktree. The deep WiX
upgrade-probe extraction path repeatedly fails during validated cleanup there,
even after the binaries and installers build successfully. Create a short,
detached exact-commit worktree under `C:\tmp`, initialize the pinned `captures`
submodule, and run the unchanged `build/Publish.ps1` from that worktree. Never
install artifacts left in `.staging` by the failed OneDrive-path run.

On managed hosts, NuGet restore and publish commonly fail in the sandbox with
`NU1900` because `https://api.nuget.org/v3/index.json` is unreachable. Retry
the exact command with approved host network access before diagnosing a package
or project failure; do not change package sources or suppress restore errors to
work around the sandbox restriction.

On Windows hosts where local execution policy blocks repository `.ps1` files,
run them in a child process with
`powershell.exe -NoProfile -ExecutionPolicy Bypass -File <script>`. This bypass
is process-scoped and must not be replaced by a machine policy change. When
running multiple scripts, check and propagate each child exit code; a parent
pipeline can otherwise return success after printing script-load errors, which
is not a valid test result.

The graphical installer success page launches the installed Tray from the
WiX bootstrapper's current user context; the Tray starts the Session Agent.
The ordinary double-click flow keeps both processes at normal integrity, but
an explicitly elevated bootstrapper makes them inherit elevation. Interactive
validation must therefore start the bundle normally, not with **Run as
administrator**. Quiet installs deliberately suppress that UI and launch. A
quiet-install validation must relaunch the installed Tray from its original
normal-integrity parent and must not report success until exactly one Tray and
one Session Agent are running. This caller-controlled relaunch does not solve
the Partner Center-owned Store invocation tracked in issue #25.

## Live tray patching on the installed machine

Use this path only when shared Contracts and both IPC versions are unchanged.
If either changed, do a coordinated full install instead. Do not repeatedly
retry a failed Program Files copy: sandbox escalation is not Windows
administrator elevation.

1. Commit the patch first so the deployed `ProductVersion` contains the exact
   commit hash, then rebuild the tray:

   ```powershell
   dotnet build src\OpenBlade.Tray\OpenBlade.Tray.csproj `
       -c Release -r win-x64 -p:NuGetAudit=false --no-restore
   ```

   The source directory is
   `src\OpenBlade.Tray\bin\Release\net10.0-windows10.0.26100.0\win-x64`.
2. Before stopping either installed process, copy the complete matching tray
   payload into a new staging directory outside `%ProgramFiles%`:
   `OpenBlade.Tray.exe`, `OpenBlade.Tray.dll`, `OpenBlade.Tray.Core.dll`,
   `OpenBlade.Session.dll`, `OpenBlade.Tray.pri`, and `MainWindow.xbf`.
   Verify every staged file exists, is non-empty, and matches the SHA-256 of
   its Release-build source. Do not stage directly into the install directory,
   and do not stop or relaunch anything if this preflight is incomplete.
   Missing `.dll`, `.pri`, or `.xbf` files can make the Session Agent or tray
   exit immediately.
3. Treat `OpenBlade.Tray.Core.dll` and `OpenBlade.Session.dll` as shared,
   process-loaded files. If either differs, stop both the installed tray and
   Session Agent after verifying their executable paths are exactly under
   `%ProgramFiles%\OpenBlade`. Stopping only the tray leaves Tray.Core locked
   by `OpenBlade.SessionAgent.exe`.
4. Use two deployment phases:
   - A normal-integrity parent launches one short-lived PowerShell child with
     `Start-Process -Verb RunAs -Wait`.
   - The elevated child stops the exact installed processes, waits for the
     target files to accept an exclusive read/write open, copies only from the
     verified staging directory, verifies all target hashes, writes a JSON
     report, and exits. It must not launch the tray or Session Agent.
   - After the elevated child exits, the normal-integrity parent must read the
     report and relaunch nothing unless all required target files exist and
     match their staged hashes. On failure, restore the complete previous
     payload or leave both processes stopped; never launch a partial payload.
   - After a successful report, the normal-integrity parent starts the
     unchanged installed Session Agent hidden with `-WorkingDirectory` set to
     the installation directory, confirms that it remains alive with the
     expected shared-DLL versions, then starts the installed tray from that same
     working directory.
5. Do not start the long-lived tray or Session Agent from the elevated child.
   They inherit elevation, Computer Use cannot inspect them normally, and the
   parent `-Wait` can remain blocked on the elevated process tree until those
   applications exit.
6. Before and after copying, record SHA-256, `ProductVersion`, and
   `FileVersion` for at least `OpenBlade.Service.exe` and
   `OpenBlade.SessionAgent.exe`; fail if either protected executable changes.
   Verify every deployed file hash equals its Release-build source.
7. Final live checks:
   - exactly one tray and one Session Agent run from `%ProgramFiles%\OpenBlade`;
   - Computer Use can inspect the tray without a higher-integrity warning;
   - tray and Tray.Core show the intended commit in `ProductVersion`;
   - the service and Session Agent executable hashes match the pre-patch report;
   - a read-only service-status query confirms the changed setting reached the
     authoritative service state. A sandboxed named-pipe probe may return
     `UnauthorizedAccessException`; run that read-only probe outside the
     sandbox after approval instead of changing pipe permissions.

Known failure signatures:

- `Access to ... Program Files\OpenBlade ... is denied`: the copy is not
  elevated. Use the single UAC phase; do not retry the same command.
- `OpenBlade.Tray.Core.dll ... is being used by another process`: the Session
  Agent is still running.
- The wrapper times out but the deployment report is complete: the elevated
  child launched a long-lived process and `-Wait` is following its process
  tree. Correct the two-phase restart; do not assume the copy failed.
- Computer Use reports that OpenBlade has higher Windows integrity: stop the
  elevated tray and Session Agent, then relaunch both from the normal parent.
- The tray starts and immediately exits after a XAML patch: verify the matching
  `.pri` and `MainWindow.xbf` were copied with the binaries.
- Media keys are temporarily dead and Event Viewer reports
  `FileNotFoundException` for `OpenBlade.Tray.Core`: the Session Agent was
  relaunched against a partial payload. Stop relaunch attempts, complete or
  restore the entire staged payload, verify all hashes, and start the Session
  Agent only after the deployment report passes.
