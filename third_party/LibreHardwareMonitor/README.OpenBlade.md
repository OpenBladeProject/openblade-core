# LibreHardwareMonitor CPU temperature source subset

This directory builds the OpenBlade CPU telemetry helper's private managed
dependency. It is a modified, deliberately limited source subset, not the
unmodified LibreHardwareMonitor package. It does not install or redistribute a
loose kernel driver.

## Pinned provenance

- LibreHardwareMonitor commit
  [`b4718a6208ebbcf028cc29f2aea30e58b138e830`](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/tree/b4718a6208ebbcf028cc29f2aea30e58b138e830).
  Official NuGet `LibreHardwareMonitorLib` **0.9.7-pre781** names that same commit
  in its repository metadata. Stable 0.9.6 lacks the Panther Lake model definition.
- Official signed PawnIO modules **0.2.11**, source commit
  [`52a7e536dff3e53c96917a28caac5e0fa6510696`](https://github.com/namazso/PawnIO.Modules/tree/52a7e536dff3e53c96917a28caac5e0fa6510696).
  Both included binaries were compared byte for byte with their files in the
  official `release_0_2_11.zip`. Their original signatures remain intact.
- `source-manifest.json` records original paths, Git blob identities, package and
  module hashes, and modified-file status. Modified source files carry dated
  notices. The corresponding module `.p` sources and complete header closure are
  in `ModulesSource`.

## Public integration API

Use `LibreHardwareMonitor.CpuTemperatureComputer`: `Open()`, `Hardware`,
`Update()`, `Close()`, and `Dispose()`. One isolated helper process owns one
instance and serializes access. No constructor or build/test step runs sensors.

`Open()` supports 64-bit Windows with an x86/x64 CPU. It initializes only CPU
topology, the PCI read mutex, and the reviewed signed CPU module. CPU sensor
discovery uses the pinned Intel model catalog (including model `0xCC`, Panther
Lake) or the pinned AMD family `0x17`, `0x19`, and `0x1A` reader. Unsupported CPU
families fail visibly. This library's CPU recognition grants no OpenBlade laptop,
fan-control, or physical-validation admission.

`Update()` clears concrete sensor values before reading and clears every value
if any update throws. Successful return with a null sensor is unavailable data.
Sensor history and min/max values never establish freshness. Consumers must use
a completed, bounded helper request and their own measurement timestamp; discard
the result on timeout or process failure. Recreate the helper after suspend/resume
or driver installation/recovery rather than reuse an uncertain device handle.

Intel exposes actual per-core temperatures and, when CPUID permits, CPU Package.
Core status `0x19C` requires its reading-valid bit. Package status `0x1B1` does
not require reserved bit 31; this follows Intel SDM Volume 4 revision 093
([official document](https://cdrdv2.intel.com/v1/dl/getContent/671098)). A required
temperature-target MSR read must succeed; its old failure-to-100 °C fallback was
removed. Targets are bounded to 60–125 °C and decoded readings to 0–125 °C.
The pinned table's explicit constants for legacy Intel models remain source
definitions. The caller should prefer a valid package reading and otherwise
require all exposed actual core channels. Distance to TjMax, averages, and cached
aggregate sensors are not emitted.

AMD exposes only the primary `Core (Tctl/Tdie)` or separate `Core (Tctl)` and
`Core (Tdie)` channels. It retains upstream's `THM_TCON_CUR_TMP` register decoding,
range adjustment, and named control offsets. Prefer Tdie when the channels differ.
No optional CCD reads or RyzenSMU object/module is included. Reading AMD SMN uses
the reviewed module's PCI index-register write followed by the data read; it is
an indexed sensor read, not a telemetry-mailbox or configuration operation.

## OpenBlade modifications

1. Replace broad Computer discovery with the CPU-only facade; remove unused
   GPU/storage/USB/motherboard and CPU load/clock/power/voltage/SMU paths.
2. Keep only typed Intel MSR reads and the AMD SMN temperature read. Raw PawnIO
   execution and the two wrappers are internal. Invalid handle, failed IOCTL,
   or a nonexact output length throws rather than returning zero-filled data.
3. Restore thread affinity and release PCI ownership in `finally`. Close failed
   module-load handles and partially constructed CPU groups deterministically.
4. Use .NET's `X86Base.CpuId` instead of allocating executable CPUID/TSC stubs.
   TSC sampling and its Linux dependency are absent from this Windows-only subset.
5. Use ephemeral settings and no sensor history. Isolate this project's build
   defaults and explicit dependency pins from OpenBlade product metadata.

Only `Microsoft.Windows.CsWin32` 0.3.369 is required at build time, with private
transitives `Microsoft.Windows.SDK.Win32Docs` 0.1.42-alpha,
`Microsoft.Windows.SDK.Win32Metadata` 71.0.45-preview, and
`Microsoft.Windows.WDK.Win32Metadata` 0.13.25-experimental. None supplies runtime
DLLs to the helper. .NET 10 provides the remaining APIs, including MutexAcl.

## Build and offline verification

```powershell
dotnet build third_party/LibreHardwareMonitor/LibreHardwareMonitorLib.csproj -p:NuGetAudit=false
dotnet test third_party/LibreHardwareMonitor/tests/OpenBlade.LibreHardwareMonitor.Tests.csproj -p:NuGetAudit=false
```

The tests exercise decoding, failed/short native-result validation, freshness,
atomic invalidation, and embedded-resource scope using synthetic values. They
never open PawnIO, execute native sensor code, install a driver, or validate
physical temperatures. A managed-host sandbox may block the test host's loopback
control channel; the same offline suite can run with ordinary approved host access.

## Redistribution

The managed source remains MPL-2.0; retain `LICENSE`, original copyright notices,
this modification notice, and corresponding source availability for the exact
distributed build. The two unchanged signed modules are LGPL-2.1-or-later; retain
their `COPYING`, module source/header closure, and ability to rebuild/relink the
open-source helper and dependency. The module headers retain their own 0BSD and
other original notices. `ModulesSource/BUILDING.md` documents compilation.
`THIRD-PARTY-NOTICES.txt` preserves the upstream notice set; `Licenses` preserves
the generator and Windows SDK/WDK notices. Release packaging must retain these
notices and offer the exact corresponding source alongside the binaries.

The separately pinned official PawnIO installer is an independent installation
recipe. Its version pin does not establish a runtime driver allowlist. Runtime
compatibility requires trusted installed-stack admission, successful signed-module
loading, and a successful typed read, with errors remaining visible.
