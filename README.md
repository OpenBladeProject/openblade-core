# OpenBlade

OpenBlade is a lightweight, standalone Windows utility for Razer Blade laptops.
Its capability system is designed for additional models, but the current
release supports only the Razer Blade 16 RZ09-0581 with USB identity
`1532:02E0` and BIOS `3.01`. The tested machine also reports EC `1.04` and
MCU `1.5.0.0`; these are recorded as diagnostic metadata and do not expand the
runtime admission scope.

## Safety state

The application is deliberately fail-closed. A PID `02E0` hardware write is
available only when that exact operation has a sanitized capture fixture that
passed decoding, interactive application, response validation, and restoration
and whose bytes are embedded and hash-trusted in the application. Stateful and
automatic controls additionally require independent readback. Setter-only
controls are exposed solely as explicit one-shot actions. Startup animation,
Function key behavior, and Gaming Mode have validated readback on the current
target, so OpenBlade can show their confirmed values, save them per profile, and
restore them during profile transitions. OpenBlade never exposes a raw-command
IPC endpoint.

If a Razer/Synapse controller process is running, the service reports an
advisory warning. OpenBlade observes confirmed device state and does not
automatically overwrite a control that another controller changed. An explicit
manual OpenBlade action may reclaim that control after a fresh readback. Users
may opt in to always restoring OpenBlade's last lighting effect; this exception
applies only to lighting and is disabled by default.

## Profiles

Saved AC, USB-C, and battery profiles can be exported and imported as JSON.
Exports use a versioned envelope with an explicit `schemaVersion`; imports are
routed through a schema-specific reader so future OpenBlade releases can migrate
older profile structures without silently interpreting them as the current one.

## Live readings

OpenBlade can show net battery power from Windows, independent Fan 1 and Fan 2
RPM values from a validated read-only HID query, firmware CPU temperature, and
GPU temperature from signed NVIDIA NVML. Each reading degrades to unavailable
when its source cannot be read. The user can choose an Off, 1-, 2-, 5-, or
10-second refresh interval and which readings appear. OpenBlade does not install
or redistribute a sensor kernel driver.

## Diagnostics

The connected tray can export a redacted diagnostic ZIP even when hardware
controls are read-only, conflicted, or unavailable. The LocalSystem service
creates a snapshot-only archive and never accepts a destination path; the
unelevated tray writes the user-selected file through a temporary sibling and
replacement move. Archives are capped at 32 KiB and contain only these entries:

- `versions.json`
- `device.json`
- `capabilities.json`
- `configuration.json`
- `command-results.json`
- `logs.jsonl`

Only bounded OpenBlade-owned log messages are retained. Raw HID reports,
payloads, checksums, transaction IDs, native handles, stack traces, serial
numbers, and configured local identity/path tokens are excluded or redacted.

## Build and test

Requires Windows 11 and the .NET 10 SDK.

Repository structure, safety constraints, and change workflow are documented in
[AGENTS.md](AGENTS.md). Product, schema, IPC, and commit-message policy is in
[VERSIONING.md](VERSIONING.md).

```powershell
dotnet restore OpenBlade.slnx
dotnet build OpenBlade.slnx --no-restore
dotnet test OpenBlade.slnx --no-build --no-restore
```

Create an untrimmed, self-contained `win-x64` bundle:

```powershell
.\build\Publish.ps1
```

The production installer bundle is written to `artifacts\win-x64\OpenBlade`.
It contains the LocalSystem Service, the per-user SessionAgent, and the Tray
application payload. The SessionAgent is the sole owner of current-user Windows
display APIs, so saved display profiles continue to follow confirmed power-source
changes after the visible tray exits. Run
`OpenBlade.Setup.exe install` from an elevated terminal to install it. Use
Windows Settings > Apps > Installed apps or the **Uninstall OpenBlade** Start
menu entry for a confirmation-based uninstall that preserves saved profiles and
preferences. For automation, the installed
`C:\Program Files\OpenBlade\OpenBlade.Setup.exe uninstall` command has the same
preserving behavior. Pass `--purge` explicitly to remove saved data too.
Uninstall requests a graceful shutdown from the exact installed tray and
SessionAgent processes and waits for both to exit before removing binaries. If
binary removal fails, Setup restores the Installed apps and Start menu retry
entries. The SessionAgent always starts at sign-in for profile automation; the
Settings toggle independently controls whether the visible tray also starts at
sign-in. Setup copies its single-file executable into the activated install tree
with write-through I/O, then verifies the installed length and SHA-256 before
committing the installation.

Interactive lighting and performance shortcuts use Raw Input in the
SessionAgent. OpenBlade does not install a UIAccess helper or run the tray as
administrator, so Reactive, Ripple, and performance-shortcut input are not
expected to react while Task Manager or another elevated application has focus.

The Capture CLI is published separately to
`artifacts\win-x64\OpenBlade.DeveloperTools`. It is a developer-only protocol
tool and is not copied into the production installer or installed under
Program Files.

## Brand assets

This repository contains only the application and tray `.ico` files required
to build and run OpenBlade. Brand source files, alternate exports, and unused
variants are maintained in
[`OSSBlade/openblade-branding`](https://github.com/OSSBlade/openblade-branding).

## Capture workflow

Synapse may remain installed for other Razer devices and can be used as the
protocol oracle while gathering captures. OpenBlade must not depend on it at
runtime. Capture evidence and tooling are maintained in
[OSSBlade/openblade-captures](https://github.com/OSSBlade/openblade-captures),
which is included here as the `captures` submodule. Capture procedures are
documented in
[OSSBlade/openblade-docs](https://github.com/OSSBlade/openblade-docs).

The capture CLI exposes typed target checks, read-only queries, fixture
validation, and explicitly named reversible validation commands; it has no raw
command mode:

```powershell
dotnet run --project src/OpenBlade.Capture -- target
dotnet run --project src/OpenBlade.Capture -- scan
dotnet run --project src/OpenBlade.Capture -- query-fan-rpm
dotnet run --project src/OpenBlade.Capture -- query-temporary-full-charge
dotnet run --project src/OpenBlade.Capture -- validate path\to\fixtures.json
```

Do not infer commands from older Blade models or admit partially validated
fixtures.
