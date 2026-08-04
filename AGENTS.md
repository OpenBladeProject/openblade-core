# OpenBlade agent guide

This file is the first instruction source for AI coding agents working in this
repository. Keep changes narrow, evidence-based, and suitable for a native
Windows hardware utility.

## Product boundary

OpenBlade is a lightweight Windows utility for Razer Blade laptops. The
architecture can admit more Blade models, but a hardware capability is enabled
only for a model and firmware combination backed by sanitized, reviewed capture
evidence. Never copy a command from another model, expose raw firmware commands,
or weaken exact-device checks to make a feature appear supported.

Production support is exact-device scoped. Current admitted targets include the
Razer Blade 16 RZ09-0581 (`1532:02E0`) and RZ09-0528 (`1532:02C6`) on their
documented firmware baselines; neither model's device-specific assumptions may
be copied to the other.

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
   service uses the same guarded Normal-mode release.
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
5. Competing Razer software is advisory by default. Do not stop it or silently
   fight external changes. Only the user-enabled lighting ownership policy may
   restore lighting automatically.
6. Keep queues bounded, cancellations observable, writes atomic, and native
   handles deterministically released. Do not add a kernel driver or depend on
   Synapse components.
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
Background pumps, loops, queues, and `ServiceClient` use
`ConfigureAwait(false)`.

xunit runs without a `SynchronizationContext`, so tests cannot distinguish
these modes or detect a wrong annotation in a UI-affine path. Do not enable
CA2007 or bulk-add `ConfigureAwait(false)` in Tray.Core.

## Hardware and capture safety

- Treat all uncaptured write commands as unsafe and unsupported.
- Read/query first. For a new write, save prior confirmed state, show the exact
  action, apply one value, read it back, and restore it.
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

1. Inspect `git status`, the affected contracts, and existing tests before
   editing. User changes and unrelated dirty files are not cleanup targets.
2. Update the smallest authoritative layer. Do not duplicate state between the
   service, Session Agent, and tray.
3. Add focused tests for parsing, normalization, failure handling, and shared UI
   contracts. Run the affected project tests before the full solution.
4. For WinUI changes, build `OpenBlade.Tray` and inspect the rendered app when
   interactive access is available. XAML contract tests do not prove visual
   quality.
5. For hardware changes, complete the documented interactive validation before
   marking support production-ready.
6. Update `CHANGELOG.md` under `Unreleased` for user-visible changes.
7. Commit and push coherent slices. Do not bundle capture evidence, product
   code, history maintenance, and unrelated formatting in one commit.
8. Preserve the line endings declared by `.editorconfig` and `.gitattributes`.
   Patch application can insert LF-only lines into an existing CRLF file, and
   `git diff --check` does not detect that mixed state. Before committing,
   inspect every touched text file for mixed endings and normalize the complete
   file to its declared convention. Treat Git's `LF will be replaced by CRLF`
   warning as unfinished work, not as a harmless staging detail.

## Versioning and commits

Follow [VERSIONING.md](VERSIONING.md). The short form is:

- `Directory.Build.props` is the product-version source of truth.
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

Use `build/Publish.ps1` and the payload validation tests for release or setup
changes. Do not install or request UAC when the user is unavailable.

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
2. Deploy the complete matching tray payload:
   `OpenBlade.Tray.exe`, `OpenBlade.Tray.dll`, `OpenBlade.Tray.Core.dll`,
   `OpenBlade.Session.dll`, `OpenBlade.Tray.pri`, and `MainWindow.xbf`.
   Missing `.pri` or `.xbf` files can make the tray exit immediately.
3. Treat `OpenBlade.Tray.Core.dll` and `OpenBlade.Session.dll` as shared,
   process-loaded files. If either differs, stop both the installed tray and
   Session Agent after verifying their executable paths are exactly under
   `%ProgramFiles%\OpenBlade`. Stopping only the tray leaves Tray.Core locked
   by `OpenBlade.SessionAgent.exe`.
4. Use two deployment phases:
   - A normal-integrity parent launches one short-lived PowerShell child with
     `Start-Process -Verb RunAs -Wait`.
   - The elevated child stops the exact installed processes, waits for the
     target files to accept an exclusive read/write open, copies the payload,
     verifies it, writes a JSON report, and exits. It must not launch the tray
     or Session Agent.
   - After the elevated child exits, the normal-integrity parent starts the
     unchanged installed Session Agent hidden, then starts the installed tray.
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
