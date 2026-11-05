# SO6120 ACPI and installed-driver transport investigation

The 2026-11-05 follow-up finds that the current laptop's ACPI tables define
another client of the host mailbox. Its mutex and completion checks do not
establish exclusive ownership for an OpenBlade adapter. The inspected Razer
driver alternatives forward separate HID feature requests; they do not supply
an owned EC transaction either.

ACPI also contains fan-control methods, but the recovered tables do not define
their actuator endpoints. These are concrete candidates with unresolved
dependencies, not validated fan controls. No firmware method, device request,
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
| DptfTabl `TFN1/TFN2._FSL` | Calls `DPTF.FNSL`, then caches requested `FSLV` without checking the result. `FNSL` maps the fan ID and calls `H_EC.UPF2`. | `UPF2` is external/called but not defined in this table set. Parent `IPFE` and fan `FND1/2` gates have unknown live values. |
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
