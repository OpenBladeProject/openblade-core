# SO6120 ACPI and installed-driver transport investigation

The 2026-11-05 follow-up finds that the current laptop's ACPI tables define
another client of the host mailbox. Its mutex and completion checks do not
establish exclusive ownership for an OpenBlade adapter. The inspected Razer
driver alternatives forward separate HID feature requests; they do not supply
an owned EC transaction either.

ACPI also contains fan-control methods, but the recovered tables do not define
their actuator endpoints. Windows exposes both Intel IPF fan participants, and
the installed signed IPF driver serializes individual primitives. Its installed
fan descriptor selects those ACPI methods in the recovered static dispatch;
live DSP selection and method execution remain unverified. This supplies no
established ownership lease or applied-state confirmation. No firmware method, device request,
port operation, installation or hardware-setting change was performed.

## Provenance and limits

Both Windows host-identity registry sources reported Razer, Blade 16 -
RZ09-0581, BIOS 4.01 dated 2026-08-20. This is provenance, not a new BIOS
admission restriction. The firmware instruction analysis uses the separately
pinned SO6120 EC 1.04 image; reading ACPI tables does not confirm which EC image
is currently running.

The DSDT was copied through Windows' documented
[GetSystemFirmwareTable](https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-getsystemfirmwaretable)
API, explicitly requesting DSDT because it was absent from the enumerated
signatures. Windows returns only the first table for a repeated signature.
Thirty-four SSDT binary values were therefore copied separately from the
read-only `HARDWARE\ACPI` registry cache. Each copied table passed its declared
length and byte-checksum checks. A cache snapshot does not establish which
conditional definitions or methods are active at runtime.

Disassembly used the official
[ACPICA 20260408 release](https://github.com/open-acpica/acpica/releases/tag/20260408),
with `iasl.exe` SHA-256
`739c597bcee4563f18d13e73b2051bb661707713e6154cf5712a1b7eef4a5df9`.
All 34 individual SSDT disassemblies completed (33 unique payloads). A combined
namespace pass failed on duplicate objects; individual output also reports external-method
dependencies. The consequential local methods were checked against mixed AML
bytes. No AML was evaluated. Missing endpoint findings are bounded to these
copied tables, not proof of absence from every runtime-loaded table.

| Input | Size in bytes | SHA-256 |
|---|---:|---|
| Windows-published DSDT | 226,774 | `76ef85960f17583b332ca450a09120c3ce5e0c9a7d1e37d059a60548040b8767` |
| EC 1.04 image | 262,144 | `8537e9fd55b7b35504d8d87b0191acc4391b67365b3fe72f6e1029a424c9e707` |
| Installed RzDev_02e0.sys 1.0.0.78 | 65,872 | `bc1666048d81a8dba6bbffdbdc86c2119632fd68ef2bb6d5d417fe3b84744787` |
| Installed RzCommon.sys 1.0.0.76 | 64,880 | `64b72ef9ce409bb17d31096c119b68c4a6a02ab35b7535d763157a4e1a587e59` |
| SysUtilsNative.dll 1.1.31.0 | 1,548,488 | `01223bfabf0355f42705836974e343a1f16f837d692999020d218b2615b8e318` |
| Cached blade2Native_v1.0.46.0.dll | 913,608 | `8c0b60c7011d6b3841bee6ef9d37719720288222abc8b656a9e494f7bdb77f4f` |

The two driver files had valid Microsoft Windows Hardware Compatibility
Publisher signatures; the DLLs had valid Razer USA Ltd. signatures. Package
copies matched the corresponding installed driver files. These checks establish
file provenance, not loaded-image identity, a currently bound device path, or
runtime API admission. Raw tables, vendor binaries, manifests and full
disassemblies remain ignored local research artifacts.

## ACPI mailbox ownership

In `\_SB.PC00.LPCB.EC0`, `MLBX` is a four-byte SystemIO operation region at
`0x300`. Its byte fields use `NoLock`: `MCMD/MDAT` are ports `300/301`, and
`MCM1/MDA1` are `302/303`. DSDT file offsets `0x36CED/0x36CF9` encode this
region and field definition. `BMRD` (`0x36F66`) and `BMWD` (`0x36F7E`) are
NotSerialized index/data helpers for the same `300/301` pair used by the
[updater](so6120-host-mailbox-contract.md).

`ECRR` (`0x36E24`) attempts to acquire the private AML mutex `ECMT` with a
1,500 ms timeout. It assigns the acquisition result to a local and proceeds
without checking it. The method clears mailbox status `94` and index/count
`91/92`, sets count, writes the four address bytes at `9C..9F`, then publishes
command `D0` at `93`. It polls command up to 200 times and requests
one-millisecond sleeps. Exhausting the loop flows into the same status-only
check as a normal exit: it does not require a final zero command. Status `80` leads to copying
the requested data bytes and attempting to release the mutex.

An AML mutex serializes only cooperating users of that mutex. A failed
acquisition does not grant ownership, and these port fields do not request the
ACPI Global Lock. See the ACPI specification's
[Acquire and Field semantics](https://uefi.org/specs/ACPI/6.5/19_ASL_Reference.html).
Neither this mutex nor the updater's application-instance mutex excludes the
other path. The raw helpers also have no whole-request acquisition.

`TBCC` (DSDT offset `0x36D46`) calls `ECRR(0x00080B82, 1)`. No caller of
`TBCC` was found in the copied DSDT/SSDT set. This establishes a defined
mailbox client and a missing cooperation contract; it does **not** establish
that Windows was executing it, or that a collision occurred during earlier
tests. No method was called to test that possibility.

## Raw-address completion differs from mapped completion

The supplied EC's `D0/D1` branch at `0x205A..0x2110` uses a 32-bit address
assembled from mailbox `9C..9F`. This differs from mapped `A0/A1` and must not
inherit its status interpretation:

- `0x2064..0x2068` first stores status `85`. Accepted `D0` copies from the
  assembled address into mailbox data; `D1` copies in the other direction.
- The loops reread count at `0x20BC/0x2100`. The descriptor is still mutable;
  there is no owner or generation in this branch.
- Normal completion reaches `0x2076..0x2088`, storing status `80` and then
  clearing command. Here `80` follows the copy, unlike the early status store
  in mapped processing.
- An initially oversized count also goes directly to that same `80`/zero
  result without copying. A zero-length request likewise copies nothing.

This identifies the semantics of the existing ACPI reader. It grants no raw
address access and supplies no independent fan readback, owner reservation,
request identity or recovery guarantee. No `D0/D1` request was submitted.

## Other ACPI access and fan candidates

`RECR/RECW` (`0x36FC2/0x37009`) are Serialized methods, but both ignore the
same `ECMT` acquisition result. They use EmbeddedControl fields `F8/F9/FA`
for command/address/data and poll without an AML timeout for command clear.
Serialization of each method is not a shared transaction lock between them.

The EC instructions narrow their reach: `0x945A` reads the ordinary page at
`0x80200`; `0x94E6` writes that page and dispatches an `F8` write to `0x9484`.
The latter implements `80/81` as read/write of `0x80700 + byte(F9)`, with
callbacks for selected indices and command clear at `0x94E0`. This is a fixed
256-byte extension page, not arbitrary access to the separate direct-duty
fields. These methods therefore do not become a direct-PWM backend merely
because their names suggest register reads and writes.

The recovered fan-shaped paths require their own admission:

| Candidate | What the copied tables establish | Missing evidence |
|---|---|---|
| DptfTabl `TFN1/TFN2._FSL` | Calls `DPTF.FNSL`, then caches requested `FSLV` without checking the result. `FNSL` maps the fan ID and calls `H_EC.UPF2`. | `UPF2` is external/called but not defined in this table set. Windows enumerates both participants, but live AML gate values and actuator behavior were not read. |
| DptfTabl `_FST` | Combines cached `FSLV` with `GFNS`; fan IDs 0/1/2 call the same parameterless `H_EC.RCFS`. | `RCFS` has no recovered implementation here. Cached intent is not independent target readback. |
| Ther_Rvp legacy `FAN0..4` | Power-resource methods reach `FNCL`, then the first two flags reach LPCB `UPFS`. | `UPFS` calls `H_EC.DUMY.UPFS`; no `DUMY` definition was recovered. |
| EC0 `PWMV` | Byte `AD` of the ordinary EC page; NVIDIA NvDDSN20 `WMAA` accesses it through a `LEDB` field. EC `0xCEEA` can copy it to PWM register `0xF01806` at `0xCF18`. | This display-related route does not establish either fan's PWM contract. |

The DSDT contains an `H_EC` stub whose `_STA` returns zero, distinct from the
real `EC0` at ports `62/66`. That does not by itself make the DPTF branch
unreachable: `FNSL` tests namespace existence with `CondRefOf`, not `_STA`.
Do not substitute `EC0` for an unresolved `H_EC` method or treat a generic fan
method name as exact-model actuator evidence.

Windows documents
[ACPI evaluation IOCTLs](https://learn.microsoft.com/en-us/windows-hardware/drivers/acpi/evaluating-acpi-control-methods-synchronously)
for drivers in the relevant device stack. A copied method definition alone
does not provide a desktop application invocation path. No existing admitted
OpenBlade interface was found that invokes these methods with the required
ownership and completion guarantees.

## Installed Razer interfaces

The exact 02E0 driver's dispatcher at VA `0x140003250` routes private control
`0x88883010` to `0x140003C68`, then helper `0x140003B90` with `0xB0191`.
Control `0x88883014` routes through `0x1400025AC` to the same helper with
`0xB0192`. These are separate lower-target feature send/receive requests.
The third device-specific control, `0x88883140`, performs target cleanup; it
does not acquire the mailbox or drain firmware workers.

The common driver's controls `0x88883040/44/48/4C` operate on an in-memory
object collection. Its dispatcher `0x140002E6C` forwards unmatched controls
to the model callback. These buffer-based APIs are not an alternate EC
transaction interface. The analyzed driver listings contain no decoded port
I/O instructions; that is corroboration of these routes, not an exhaustive
proof against every computed path.

The recovered `SysUtilsNative` DeviceIoControl caller at `0x18005FA10` opens
a shared HID path, sends a fixed 91-byte feature report, sleeps, and separately
reads the response through `0xB0192`/GetOverlappedResult. Its protocol checks
identify an identification-string request. It is not an EC duty API. The
cached Blade native DLL export inventory likewise establishes no owned EC
interface; the module was not subjected to a complete internal-code audit.

Changing to these private Razer IOCTLs would retain the already established
[MCU shared-transfer problem](so6120-mcu-transfer-ownership.md), and would
add a vendor-driver dependency without solving it. None is a justified live
backend for the generic Auto exit coordinator.

## Installed Intel IPF route

Read-only Windows PnP metadata resolves a previously open question: both
`ACPI\INTC10D6\TFN1` and `ACPI\INTC10D6\TFN2` are present, report Status OK
and ProblemCode zero, and bind to `ipf_acpi` through `oem237.inf`, version
2.3.20303.5058. Their location paths end in `IETM/TFN1` and `IETM/TFN2`.
The manager and four sensor participants are also present. Legacy `FAN0..4`
enumerate separately under `machine.inf`. Enumeration establishes device
presence and binding, not successful `_FSL` evaluation or fan actuation.

The installed IPF files were read as data. Both driver signatures were Valid,
Microsoft Windows Hardware Compatibility Publisher. No IPF device was opened.

| Input | Size in bytes | SHA-256 |
|---|---:|---|
| ipf_acpi.sys 2.3.20303.5058 | 90,600 | `39a206516636d91f95b7ad88373b4613e5f193324f247d697b1863ac041ba658` |
| ipf_cpu.sys 2.3.20303.5058 | 552,928 | `6692e265ca8f906ddb31f9f80a1d81cfca40f799b16ffc608dea671ee637363b` |
| Installed dsp.dv | 778,547 | `86dc900633399b2cfcbf678f535f9887a3301c99492908df67a8cac88ee634bc` |
| Extracted ipf_fan.edp | 6,347 | `1c9837b802e3f3c6a47df6f7c9f1a6159a35f00e69d3075472a6a17402c13bec` |

The installed data vault passes its embedded SHA-256 check and all 35 record
bounds. Its `ipf_fan.edp` compact primitive catalog has 21 bounded entries;
these four use domain `D0`, instance 255, action type 4 (ACPI):

| Primitive | Operation | Method | Method-selector offset in EDP |
|---|---|---|---|
| 84 | GET_FAN_INFORMATION | `_FIF` | `0xD0` |
| 85 | GET_FAN_PERFORMANCE_STATES | `_FPS` | `0x16C` |
| 86 | GET_FAN_STATUS | `_FST` | `0x2A4` |
| 87 | SET_FAN_LEVEL | `_FSL` | `0x374` |

The layouts and enums were cross-checked against Intel's public source at
[468699545442788999071d0a8ccb71b35332cdf1](https://github.com/intel/dptf/tree/468699545442788999071d0a8ccb71b35332cdf1).
That source is not the exact Windows binary implementation. The installed
full package header differs from the public layout, so only the data-vault
records and compact primitive catalog are decoded here. An `INTC10D6`-matching
literal in the package is corroboration, not proof of live DSP selection or
the current lower-framework participant IDs. A second parser independently
confirmed the vault hash, record boundaries and four method mappings.

Static `ipf_cpu.sys` analysis establishes this path, with image VAs below:

1. `0x140076610` creates `ipf_lf`; `0x140040FB8` configures a sequential WDF
   queue. Handler `0x140040A80` accepts control `0x120004` and validates the
   packed IPC extent before dispatch. NTSTATUS, outer IPC result and primitive
   result are separate fields; a successful DeviceIoControl is not enough.
2. Primitive dispatcher `0x14002714C` validates payload bounds and selects a
   participant. Helpers `0x140025A2C/0x140025B88` acquire/release its WDF wait
   lock around one primitive. Lookup `0x1400275A0` requires participant state
   4 and resolves the DSP action. This lock ends before any later request.
3. Action descriptor `0x14006E560` selects ACPI GET `0x1400068F0` or SET
   `0x1400073C0`. The ordinary SET path used by `_FSL` builds arguments and
   calls evaluator `0x14003D178`. A nonnegative ACPI transport result becomes
   ESIF success; it does not issue `_FST` or confirm the applied fan state.
4. The separate GET validates the ACPI return structure and unpacks it. Local
   and scoped-child evaluation helpers send standard ACPI requests through the
   participant's lower target using `0x14003BF6C`. The participant wait and
   synchronous WDF send use null timeout/options, so this path does not
   establish a client-selected completion deadline.

The WDF call-table and dispatch values were checked against Microsoft's
[function table](https://github.com/microsoft/Windows-Driver-Frameworks/blob/main/src/publicinc/wdf/kmdf/1.15/wdffuncenum.h)
and [queue semantics](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/wdfio/ne-wdfio-_wdf_io_queue_dispatch_type).
The compiled creation ACL was identified, but the live ACL was not inspected.
No complete policy handoff or lease spanning SET, independent readback and
restoration was established. The queue and participant lock do not reserve the
participant across separate requests or coordinate other ACPI entry points.

This is an existing signed transport worth distinguishing from generic port
I/O. This installed fan descriptor selects the unresolved AML actuators above
in the recovered static dispatch; it supplies no second raw EC or PWM implementation.
Neither optional PGMB mapping nor opaque participant metadata establishes such
an implementation. Existing event-log sampling provided no fan-method result
that would close the gap. No generic IPF or ACPI invocation endpoint is added.

## BIOS 4.01 image cross-check

The existing official-model BIOS carrier was inspected as data. Authenticode
reported Valid, Razer USA Ltd.; recorded download-origin metadata names
`RazerBlade16RZ09-0581/SO6120_BIOSv0401.exe` on `dl.razerzone.com`.
Independent NSIS extraction decoded all 11 compressed blocks through EOF and
confirmed the container CRC. Block 5 matches the existing BIOS payload byte
for byte. Neither executable nor BIOS payload was run or flashed.

| Input | Size in bytes | SHA-256 |
|---|---:|---|
| SO6120_BIOSv0401.exe | 9,900,296 | `29687c5465c12c2023b6c30f3cd8f818f9c5258ca5d01944d3782c3c9e28b839` |
| SO6120_BIOSv401.bin | 30,474,368 | `db08e4cb4ef3121a4b4b5df0a03f7636245240caa3c9e4aee87982db7a9ab77d` |
| Reconstructed FV_MAIN_WRAPPER | 7,012,352 | `4043cf5f09290a962738963666f63617be2a356ed8e23dec900a6d10b8a099a1` |

The `.bin` begins with an AMI PFAT/BIOS Guard container, not a contiguous SPI
image. Its declared component blocks interleave data with headers, update
scripts and signatures. Feeding the wrapper directly into UEFIExtract made
the main compressed section appear corrupt at the first intervening block
signature. The reconstruction used the format described by
[BIOSUtilities at f799425af93264b62a2f3c3cc5bbabf8a2226114](https://github.com/platomav/BIOSUtilities/blob/f799425af93264b62a2f3c3cc5bbabf8a2226114/biosutilities/ami_pfat_extract.py),
with explicit length and boundary checks. A separate implementation checked
all 20 declared components / 29 blocks against a fresh carrier extraction.
That validates byte provenance and layout, not each BIOS Guard signature.

The main component has six 1 MiB blocks and one 720,896-byte block. Rebuilding
their data stream lets the official
[UEFIExtract A76](https://github.com/LongSoft/UEFITool/releases/tag/A76) decode
the main volume without a decompression failure. Its missing Volume Top File
warning applies to this extracted component; it is not a complete flash image.
The declared PFAT components end at byte 20,029,565. The 10,444,803-byte tail
is outside that component reconstruction, so this is not a complete-container
absence proof.

An independent scan of all 1,481 extracted main-volume bodies recovered 67
distinct checksum-valid DSDT/SSDT tables. Only the DSDT and DptfTabl contain
`DUMY`, `UPF2` or `RCFS`; the relevant names are external declarations or
calls, with no recovered actuator definition. This extends the earlier
Windows-table search to the shipped main-volume AML, not every possible
runtime-generated namespace object or native SMM implementation.

The exact method packages for the two `_FSL/_FST/_FPS` sets, `FNSL/GFNS`,
the checked parent/fan `_STA` methods, legacy `UPFS/FNCL` and mailbox
`TBCC/ECRR/BMRD/BMWD` match the Windows copies byte for byte. Whole tables
do differ: DptfTabl has a patched NVS region and checksum; DSDT has platform
constants, NVS/resource values and two PCI `_ADR` changes. These differences
do not change the checked fan or mailbox method bytes, but initialized values
can still affect live behavior. The recovered BIOS corroborates those paths
without filling in their missing actuator endpoints.

## Native BIOS ownership and completion

The main volume also supplies native code for the AML `GENS`/SMI candidate
and another boot-time EC client. Addresses in this section are module RVAs,
not runtime addresses. The analyzed PE images have preferred image base zero.

| Module | Size in bytes | SHA-256 |
|---|---:|---|
| ACPISMM | 8,192 | `66c1c9a4f7617951355d9882a648e1ab1ed7e75b9bb26a6c344cc340beddb0ca` |
| PanelColorProfileSmm | 6,656 | `68d8438d05964727cb62f75ccb8a6b923b1bc9514243de3d50722cc6041ac2bd` |
| RazerAcpiDxe | 11,264 | `3de549178dd4f77bf3e5f61b844a95276884352b3a2c8daccc972c70bd91cb52` |

`ACPISMM!0x1504` registers software SMI `E1` and publishes a callback
registration protocol. Dispatcher `0x14C8` reads the DWORD command from the
shared ACPI buffer, requires an installed callback below index 48, and calls
it with the buffer at offset four. It then returns success without preserving
the callback's return value. Registration at `0x14A4` stores callbacks; it
does not implement an EC transaction or acquire a mailbox owner.

The exact registration-protocol GUID appears in only one other extracted
main-volume PE body: `PanelColorProfileSmm`. Its `0x14BC` routine registers
command 3 to callback `0x13C0`. That callback uses SMM Variable services to
attempt to set `OemSetupVar` to a one-byte value of 1 under GUID
`633AAE9A-EFA4-4798-8F81-9F67059506F4`. It ignores the ACPI argument payload
and attempts the write only after its protocol, allocation and variable-read
checks pass. It performs no EC port transaction. Thus the recovered AML `WMBA` branch
calling `GENS(3, 0, 0)` reaches a panel setup-variable operation if registration
succeeds, not a fan setter. An SMI returning to AML does not establish an
applied fan state. No SMI was invoked.

`RazerAcpiDxe!0x12AC` is a Boot Services initializer. It allocates ACPI NVS
buffers, retrieves EC data and publishes lengths/addresses. Its literal port
rules distinguish another transaction from the `93/94` mailbox:

- Helpers `0x161C/0x165C` use index/data ports `300/301`; `0x163C/0x1680`
  use `302/303`. The pairs contain no whole-request owner acquisition.
  Startup `0x16A4` clears bit 5 of index `15` through the first pair; this is
  not established as an OS ownership token.
- Helper `0x1714` waits up to 200 polls for bit 0 of secondary index `19` to
  clear. Transfer `0x17A0` then writes its EC command to index `13` and `F0`
  to index `12`. Commands `E0/E1/E8/D0` here are data selectors on this
  separate path, not software SMI `E1` or raw-address mailbox command `D0`.
- Helper `0x1774` polls secondary status `1B` for bit 7, returning `FF` on
  expiry. A zero low nibble allows copying a declared length from fixed
  MMIO `FE0B8000`, at most `800` bytes per chunk. Status bit 6 triggers an
  `F1` continuation; that outer continuation loop has no established total
  iteration bound.
- Cleanup `0x1744` writes `F2` to index `12` and polls it for zero up to
  200 times. It returns void on both success and expiry; the transfer returns
  its earlier status without confirming cleanup. Timer-delay arguments do
  not establish a complete wall-clock deadline.

This is evidence of a vendor boot data-read sequence, not an exposed Windows
fan API or proof of current runtime contention. It establishes no transferable
ownership lease for either port pair. The subsequent
[EC-side secondary mailbox analysis](so6120-secondary-mailbox-contract.md)
identifies stored-record transfers and a conditional 32-chunk bound, while
confirming that the busy flag does not reject a replacement start and cleanup
does not restore fan control. The paired write family includes persistent
thermal tables; it is not an admitted runtime PWM interface. These commands
must not inherit the original mailbox's contract.

## Remaining boundary

The additional sources refine the ownership requirements rather than remove
them. A usable route must establish a complete transaction owner across its
actual participants, bounded completion tied to that request, independent
readback, and the separately required
[cooling recovery](so6120-direct-duty-safety.md#decision-and-remaining-evidence).
An AML mutex name, a cached fan level or a private driver control number does
not supply those properties. The unresolved ACPI actuator dependencies and
runtime namespace remain specific questions; broad claims that ACPI has no
fan controls, or that stopping a user-mode application excludes every mailbox
client, are unsupported.
