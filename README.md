# OpenSynapse

OpenSynapse is a lightweight, standalone Synapse replacement targeting only the
Razer Blade 16 RZ09-0581 with USB identity `1532:02E0` and BIOS `3.01`.

## Safety state

The application is deliberately fail-closed. Device discovery is read-only and
all hardware writes remain unavailable until each PID `02E0` operation has a
sanitized capture fixture that passed decoding, interactive application,
readback, and restoration. OpenSynapse never exposes a raw-command IPC endpoint.

If a Razer/Synapse controller process is running, the service reports the
conflict and performs zero hardware writes.

## Build and test

Requires Windows 11 and the .NET 10 SDK.

```powershell
dotnet restore OpenSynapse.slnx
dotnet build OpenSynapse.slnx --no-restore
dotnet test OpenSynapse.slnx --no-build --no-restore
```

Create an untrimmed, self-contained `win-x64` bundle:

```powershell
.\build\Publish.ps1
```

The bundle is written to `artifacts\win-x64\OpenSynapse`. Run
`OpenSynapse.Setup.exe install` from an elevated terminal to install it. Use
`uninstall` to preserve profiles or `uninstall --purge` to remove them.

## Capture workflow

Keep Synapse installed only while gathering protocol captures. After the
USBPcap filter is active, follow [captures/README.md](captures/README.md) and
capture exactly one UI operation at a time. The capture CLI can perform only
target display, read-only discovery, and fixture validation:

```powershell
dotnet run --project src/OpenSynapse.Capture -- target
dotnet run --project src/OpenSynapse.Capture -- scan
dotnet run --project src/OpenSynapse.Capture -- validate captures/fixtures.json
```

Do not infer commands from older Blade models or admit partially validated
fixtures.
