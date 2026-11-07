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

The current installed endpoint therefore has not provided a usable CPU reading.
The next source task is to reconcile Intel's actual client transport and device
routing with that failure. No initialization command or alternate IOCTL should
be guessed. Once that contract is established, a bounded diagnostic can check
response types, lengths, status, units, elapsed time and variation. A successful
diagnostic would establish working readings under the observed conditions; it
would not by itself enable Stop or validate other laptops.

## Alternatives ruled out for now

The EC firmware genuinely obtains CPU temperature through PECI, but the exposed
raw and filtered bytes can retain an old value after a failed transaction. No
reviewed host-visible CPU sample generation or success age was found. A freshly
read EC byte therefore cannot yet protect Stop against stale producer data.
Other ACPI zones also contain constants or fallback paths. Neither is a suitable
replacement merely because Windows returns a successful read.
