# OpenBlade

OpenBlade is a lightweight, standalone Synapse replacement targeting only the
Razer Blade 16 RZ09-0581 with USB identity `1532:02E0` and BIOS `3.01`.

## Safety state

The application is deliberately fail-closed. A PID `02E0` hardware write is
available only when that exact operation has a sanitized capture fixture that
passed decoding, interactive application, readback, and restoration and whose
bytes are embedded and hash-trusted in the application. OpenBlade never exposes
a raw-command IPC endpoint.

If a Razer/Synapse controller process is running, the service reports an
advisory warning. OpenBlade observes confirmed device state and does not
automatically overwrite a control that another controller changed. An explicit
manual OpenBlade action may reclaim that control after a fresh readback.

## Live readings

OpenBlade can show net battery power from Windows and independent Fan 1 and
Fan 2 RPM values from a validated, read-only HID query on this exact device.
Each reading degrades to unavailable when its source cannot be read. The user
can choose an Off, 1-, 2-, 5-, or 10-second refresh interval; collection stops
while the window is closed to the tray. OpenBlade does not collect CPU or GPU
temperature and does not install a sensor kernel driver.

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
It contains only the Service and Tray application payload. Run
`OpenBlade.Setup.exe install` from an elevated terminal to install it. Use
`OpenBlade.Setup.exe uninstall` to preserve profiles or
`OpenBlade.Setup.exe uninstall --purge` to remove them. Uninstall requests a
graceful shutdown from the installed tray process and waits for that exact
executable to exit before removing binaries.

The Capture CLI is published separately to
`artifacts\win-x64\OpenBlade.DeveloperTools`. It is a developer-only protocol
tool and is not copied into the production installer or installed under
Program Files.

## Brand assets

This repository contains only the application and tray `.ico` files required
to build and run OpenBlade. Brand source files, alternate exports, and unused
variants are maintained in
[`boyuanx/openblade-branding`](https://github.com/boyuanx/openblade-branding).

## Capture workflow

Synapse may remain installed for other Razer devices and can be used as the
protocol oracle while gathering captures. OpenBlade must not depend on it at
runtime. After the USBPcap filter is active, follow
[captures/README.md](captures/README.md) and capture exactly one UI operation at
a time. The capture CLI exposes typed target checks, read-only queries, fixture
validation, and explicitly named reversible validation commands; it has no raw
command mode:

```powershell
dotnet run --project src/OpenBlade.Capture -- target
dotnet run --project src/OpenBlade.Capture -- scan
dotnet run --project src/OpenBlade.Capture -- query-fan-rpm
dotnet run --project src/OpenBlade.Capture -- query-temporary-full-charge
dotnet run --project src/OpenBlade.Capture -- validate captures/fixtures.json
```

Do not infer commands from older Blade models or admit partially validated
fixtures.
