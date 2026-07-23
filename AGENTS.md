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

The current production target is the Razer Blade 16 RZ09-0581, USB
`1532:02E0`, on Windows 11 x64.

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
- `src/OpenBlade.Setup`: transactional install, repair, shutdown, and uninstall.
- `tests/*`: project-aligned tests plus `Build.Tests` release-payload checks.
- `captures`: submodule containing sanitized evidence and capture tooling.
- `build`: publish, package, and deployment scripts.

## Architecture rules

1. The service is the sole owner of the laptop control HID handle. It exposes
   typed operations only and serializes all hardware access.
2. The Session Agent owns current-user APIs and input observation. The visible
   tray may exit without disabling profile automation, display automation,
   interactive lighting, or configured special keys.
3. UI-only work must not rebuild, replace, or advance the installed service or
   Session Agent. Use the tray-only deployment path and verify protected hashes.
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
