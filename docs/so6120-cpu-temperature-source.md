# CPU temperature for Stop

## Imported correction

This branch incorporates the shared source and fan-curve regression changes from
[07bd37c0](https://github.com/OpenBladeProject/openblade-core/commit/07bd37c0f4066bab39755f7ba40e778ae1c6e369)
in PR #890, preserving the Stop-specific GPU power/session handling already here.
The correction applies to every consumer of `SafeWindowsFanTemperatureSource`,
including dashboard telemetry, CPU-dependent fan curves and the native guardian.

The exact RZ09-0581 BIOS 4.01 and RZ09-0528 BIOS 2.02 DSDTs return the literal
`0x0C6E` from their `TZRZ._TMP` methods. This is 3182 tenths kelvin, or 45.05 C.
The retained live RZ09-0581 table agrees. See the
[pinned source review](https://github.com/OpenBladeProject/openblade-core/blob/07bd37c0f4066bab39755f7ba40e778ae1c6e369/docs/bios-ec-source-validation.md).
A read-only check on the RZ09-0581 on 2026-11-07 returned 3182 with valid PDH
status in all three one-second samples. Successful collection does not turn
the firmware constant into a measured CPU temperature.

Both known paths are excluded at the shared reader. Generic conversion still
accepts a legitimate measured 45 C; the correction rejects the source, not the
numeric value. CPU-dependent fan curves retain their existing device-admitted
maximum-speed fallback. Stop entry requires a valid CPU measurement, while
loss of CPU telemetry during an owned Stop ends its thermal policy immediately
as a safety condition, independently of the user-configured hot-temperature delay.

## Replacement requirements

The present machine reports an Intel Core Ultra 9 386H and Blade 16 RZ09-0581.
A replacement must identify a measured CPU/package sensor and its units, expose
producer freshness and failure separately from cached values, and remain readable
by the surviving native owner while it owns the EC interface. Re-reading a cache
quickly is not evidence that its producer sampled the CPU recently.

No alternate zone, new driver, vendor telemetry dependency, or new firmware
access is admitted by removing the invalid sources. No monitor was launched,
no driver was installed, and no fan command was issued for this investigation.

## Standalone investigation

The replacement should work without HWiNFO or another monitoring application.
The strongest candidate is the Intel Innovation Platform Framework (IPF) driver
already installed on this Intel machine. It offers a temperature GET independent
of Synapse, without adding a kernel driver. This remains a candidate; the branch
does not yet use it for telemetry or Stop.

Offline analysis of installed IPF 2.3.20303.5058 establishes:

- Its `ipf_lf` interface has a serialized, typed ESIF primitive transport. The
  processor descriptor's D0 / instance 255 `GET_TEMPERATURE` (14) resolves through
  KDELEGATE to a new ACPI evaluation, or the driver's MMIO/MSR temperature paths.
- The exact BIOS's separate `TCPU._TMP` reads an eight-bit SystemMemory field at
  MCHBAR + `0x5978` and converts it to tenths kelvin. It does not read the constant
  `TZRZ` method or the EC's cached CPU byte.
- The driver copies a new result only on success and propagates acquisition
  failure. Its historical maximum is stored separately, not returned as a
  fallback. The hardware read paths dereference mapped registers or execute
  `RDMSR`; they do not reuse a previously successful software sample.
- Metadata GETs expose the active participant ID, state, PCI/ACPI identity and
  selected DSP. These must identify the current processor before a temperature
  request. Neither a display name nor participant ordering establishes identity.
- ESIF's temperature representation is uint32 tenths kelvin with the vendor
  conversion `(value - 2732) / 10`. Errors, invalid values and malformed responses
  must remain unavailable. It does not expose a separate sensor timestamp.

The reviewed driver SHA-256 is
`6692e265ca8f906ddb31f9f80a1d81cfca40f799b16ffc608dea671ee637363b`;
the installed DSP carrier SHA-256 is
`86dc900633399b2cfcbf678f535f9887a3301c99492908df67a8cac88ee634bc`.
These identify the offline analysis and diagnostic input, not a production driver
version allowlist. Public ABI corroboration uses
[Intel DPTF source at 46869954](https://github.com/intel/dptf/tree/468699545442788999071d0a8ccb71b35332cdf1),
including `esif_ipc.h`, `esif_command.h` and `Temperature.cpp`; compiled Windows
handlers were checked independently rather than assuming source ABI compatibility.

There are still limits to establish before admitting it for Stop. GET14 can
internally select ACPI, MMIO or MSR without identifying its effective backend in
the scalar result. The installed Panther Lake descriptor extracts bits 23:16
from package MSR `0x1B1`, while Intel's architectural definition describes bits
22:16 and reserves bit 23. Bit 31 is reserved in the package register: the core
register's Reading Valid rule must not be applied to it. A client must not invent
a correction to an already converted vendor result. See
[Intel SDM Volume 4, revision 093](https://cdrdv2.intel.com/v1/dl/getContent/671098).
Intel's public Series 3 register table documents PP0 temperature at `0x597C`,
which does not independently establish the distinct `0x5978` field used here.
[Intel Series 3 register definition](https://edc.intel.com/content/www/us/en/design/publications/core-ultra-series-3-processors-cfg-and-mem-registers/001/-pp0-temperature-0-0-0-mchbar-pcu-offset-597c/)

The native transport also lacks a client-selected completion deadline. A future
provider must isolate acquisition so a blocked temperature query cannot block
the guardian's Auto restoration. Read completion, freshness, failure and
power/session invalidation need explicit treatment.

An authorized read-only diagnostic on 2026-11-07 passed exact host, CPU capability,
running driver/signature/binding and DSP checks. Opening `ipf_lf` succeeded, but
the first metadata GET5 failed with Win32 `ERROR_INVALID_FUNCTION` (1), zero
returned bytes and an unchanged inner-result sentinel. The diagnostic stopped;
neither GET6 nor a temperature GET14 was sent. No fan/profile setting changed.
Independent namespace inspection confirmed the expected `ipf_lf` device target.

Follow-up static analysis explains this result. The installed `ipf_umdf2.dll`
upper filter registers an external device-control callback that accepts only
`0x80862000`; other codes, including the older kernel IPC `0x120004`, complete
with `STATUS_NOT_IMPLEMENTED`. This matches the observed Win32 error. Intel's
own older client helper uses the same open flags and IPC layout as the diagnostic;
no missing per-handle handshake was found. The filter's accepted external code
requires event IPC, so it is not an alternate temperature/metadata endpoint.
Its internal kernel IPC uses a WDF I/O target unavailable through the recovered
user-mode interface. The reviewed filter SHA-256 is
`0a876dc65e1f011aa9a1f79f39343abbe2569bc4937f22af58187c9986fcaafc`.

This older endpoint cannot provide the diagnostic's reading through the installed
filter. Do not remove or bypass that filter, guess an initialization command, or
use the event endpoint for a temperature request. The separately reviewed public
client API below successfully reached the running Intel service instead.

## Successful standalone Intel service observation

On 2026-11-07, a separately authorized diagnostic used the existing signed
`ipfcore.dll` / `ipfipc.dll` public C interface. It completed ten CPU temperature
reads, one second apart, without installing a component or sending a fan/profile
command:

| Reading | CPU temperature (C) |
| --- | ---: |
| 1 | 70 |
| 2 | 84 |
| 3 | 77 |
| 4 | 83 |
| 5 | 77 |
| 6 | 68 |
| 7 | 73 |
| 8 | 74 |
| 9 | 78 |
| 10 | 76 |

All ten returned ESIF success, temperature type 6 and four bytes. The slowest
temperature read took 16.3994 ms. The native session disconnected and the worker
exited successfully, with no timeout. The workload was not controlled or used
as a calibration reference. These are changing readings from the identified CPU
participant, not the earlier constant TZRZ source.

The client used the public SDK version `1.0.11402` in the installed
`GetIpfInterface` version-1 ABI (156 bytes). A passive AppInterface version-4
observer received the library's actual participant metadata: enabled `TCPU`,
PCI `8086:B01D`, and the TCPU ACPI scope. It rejected absent, duplicate, removed
or malformed CPU identity before any temperature query. The only sensor command
was fixed `execute-primitive TCPU GET_TEMPERATURE D0 255`, with a void request
and bounded four-byte temperature response. The library retains its ordinary
participant bookkeeping; its normal lifecycle also subscribes/unsubscribes to
the logging event without changing log verbosity or thermal settings.

Reviewed client SHA-256 values:

| File | SHA-256 |
| --- | --- |
| `ipfcore.dll` | `aee37dd796c076d6c31895e995bc7d5b5e6689bd46772ddee33ad4b7feabcfa3` |
| `ipfipc.dll` | `cf2eafb2c71a35657f83e1b9e425edd1de52d222bfbfa637ff6255cb001e9113` |

The diagnostic checked Windows signature trust, package binding and registry
installation path, held the client files against modification, rehashed their
locked contents, and verified actual loaded module paths before connecting.
It used an isolated worker with independent controller-lifetime monitoring.
Offline checks covered ABI/SDK fields, participant lifecycle and error/response
rejection (17 assertions plus packing); the worker-exit harness also passed.

This establishes a working standalone CPU source on the observed host and
installed Intel stack. It has **not** been connected to production telemetry or
Stop, and the correction has not been locally installed. The next implementation
is a bounded reader shared by telemetry and the surviving Stop owner, with
explicit unavailable/stale results, cancellation, power/session invalidation and
native-client lifetime handling. The observed elevated diagnostic does not prove
that production requires elevation. No other model, future package contract,
surviving guardian session or lifecycle transition inherits this observation.

## How other Windows monitors obtain temperatures

Online primary-source review on 2026-11-07 found these approaches:

| Approach | Actual source and relevance |
| --- | --- |
| `lfreist/hwinfo` | This separate MIT-licensed C++ library can be redistributed with its notices, but has no CPU temperature API at reviewed revision `acdc2eb4`. CPU data covers identification, topology, frequency and utilization; the temperature field is part of GPU status. Its Windows CPU implementation has no AMD thermal-sensor backend. It therefore cannot supply Stop's CPU temperature. [CPU interface](https://github.com/lfreist/hwinfo/blob/acdc2eb4a9109b4ea6bbe207e03d9da33bc16fab/include/hwinfo/cpu.h), [monitoring interface](https://github.com/lfreist/hwinfo/blob/acdc2eb4a9109b4ea6bbe207e03d9da33bc16fab/include/hwinfo/monitoring.h), [Windows CPU implementation](https://github.com/lfreist/hwinfo/blob/acdc2eb4a9109b4ea6bbe207e03d9da33bc16fab/src/windows/cpu.cpp), [license](https://github.com/lfreist/hwinfo/blob/acdc2eb4a9109b4ea6bbe207e03d9da33bc16fab/LICENSE). |
| LibreHardwareMonitor | The Intel implementation recognizes Panther Lake and reads CPU MSRs through PawnIO. Its AMD Zen implementation reads the SMN temperature register and exposes Tctl/Tdie/CCD sensors. Both require the separate PawnIO kernel driver; a user-mode library reference alone is not the complete solution. At reviewed revision `b4718a62`: [Intel reader](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/b4718a6208ebbcf028cc29f2aea30e58b138e830/LibreHardwareMonitorLib/Hardware/Cpu/IntelCpu.cs), [AMD reader](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/b4718a6208ebbcf028cc29f2aea30e58b138e830/LibreHardwareMonitorLib/Hardware/Cpu/Amd17Cpu.cs), [PawnIO transport](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/b4718a6208ebbcf028cc29f2aea30e58b138e830/LibreHardwareMonitorLib/PawnIo/PawnIo.cs). |
| Intel Power Gadget | Its Windows API obtains temperature/MSR data through its installed driver. The legacy guide does not establish Core Ultra Series 3 support. [Intel API guide](https://www.intel.com/content/www/us/en/developer/articles/training/using-the-intel-power-gadget-30-api-on-windows.html). |
| Standard WMI temperature probe | Microsoft says `Win32_TemperatureProbe.CurrentReading` is not populated by current WMI implementations because SMBIOS cannot supply real-time readings. This is distinct from model-specific ACPI zones, whose firmware semantics still need review. [Microsoft documentation](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-temperatureprobe). |
| HWiNFO SDK | A separately licensed monitoring DLL intended for integration, including operation without a GUI and automated installation support. Bundling needs the licensor's explicit approval; a normal Pro license does not include this separate SDK. Its redistribution terms and pricing must be agreed before packaging. [SDK](https://www.hwinfo.com/sdk/), [author's clarification](https://www.hwinfo.com/forum/threads/sdk.10694/), [EULA](https://www.hwinfo.com/files/license.pdf). |

The successful existing Intel service route currently avoids those additional
monitoring and driver dependencies on this laptop. No HWiNFO component has been
acquired, bundled, installed or invoked.

For AMD support, share the CPU-temperature result and policy contract while
selecting a reviewed processor-specific provider. The working Intel IPF route
does not establish an AMD backend. LibreHardwareMonitor is a relevant source
to evaluate if a new driver becomes acceptable; this research does not add that
dependency or establish its sensor behavior on an AMD Blade.

## Deployment and processor coverage

PawnIO supplies privileged hardware access and executes signed access modules;
LibreHardwareMonitor supplies CPU identification, processor-specific sensor
access and conversion to temperatures. For processors it supports, OpenBlade
can consume that library instead of implementing each register map. Integration
still needs explicit sensor selection, failed/stale-sample handling and validation
on the supported Blade hardware. Installing PawnIO alone does not add a CPU
temperature API or guarantee coverage of every new processor.

LibreHardwareMonitor's MPL-2.0 license permits binary distribution while retaining
notices and making the corresponding covered source available. Its embedded
PawnIO modules and other dependencies retain their own notices and source terms.
The official signed PawnIO binary edition has separate terms: the author allows
redistribution of the **unmodified installer**, not repackaging the extracted
driver as a private app component. See the [MPL terms](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/b4718a6208ebbcf028cc29f2aea30e58b138e830/LICENSE),
[PawnIO licensing guide](https://github.com/namazso/PawnIO.Modules/wiki/Using-PawnIO-Modules#licensing-considerations)
and [author's redistribution clarification](https://github.com/a1ive/nwinfo/issues/86).

One OpenBlade setup could include the library and verified official installer,
reuse a compatible existing PawnIO installation, or install it during the normal
elevated setup. No separate user download or wizard is inherently required.
Silent installation is supported; PawnIO 2.2.0 explicitly returns a restart-needed
result when applicable. Setup must handle that outcome before enabling telemetry.
PawnIO is shared with other applications, so OpenBlade should not unconditionally
remove it on uninstall. This is a feasible packaging design, not an implemented
installer or a change to the repository's current no-new-driver policy.
See [upstream silent-install guidance](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/discussions/1904)
and [PawnIO 2.2.0 release notes](https://github.com/namazso/PawnIO.Setup/releases/tag/2.2.0).

The Intel prototype depends on the existing compatible IPF client libraries
(`ipfcore.dll`, `ipfipc.dll`), running Intel public service, processor/platform
drivers and an enabled processor participant implementing the reviewed
temperature GET. On the observed machine the stack is IPF 2.3.20303.5058, with
`ipf_cpu`, `ipf_acpi` and `ipfsvc`; these are observation provenance, not a claim
that every driver build or Intel processor is compatible. The prototype remains
diagnostic code, not a shipped reader.

Intel describes IPF/DTT as an OEM-configured platform solution whose capabilities
depend on firmware and platform drivers. A modern Intel CPU alone therefore does
not establish availability of this API or a usable temperature participant.
Only the RZ09-0581 / Core Ultra 9 386H combination has been exercised here.
Other Intel systems need compatible-stack discovery and source/measurement
validation; AMD requires another provider. See [Intel's platform requirements](https://www.intel.com/content/www/us/en/support/articles/000102775/processors.html).

## Alternatives ruled out for now

The EC firmware genuinely obtains CPU temperature through PECI, but the exposed
raw and filtered bytes can retain an old value after a failed transaction. No
reviewed host-visible CPU sample generation or success age was found. A freshly
read EC byte therefore cannot yet protect Stop against stale producer data.
Other ACPI zones also contain constants or fallback paths. Neither is a suitable
replacement merely because Windows returns a successful read.
