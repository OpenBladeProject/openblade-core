# Missing-model BIOS host EC interfaces

Reviewed offline on 2026-11-08 against core
`10caac37c5868ee8bbc4a54a392141facc5b5a34`. This follows the pinned inputs in
[Blade 14](fan-stop-blade14-missing-ec-evidence.md) and
[Blade 15](fan-stop-blade15-missing-ec-evidence.md). It traces host interfaces
rather than repeating payload-marker searches. **The BIOSes establish partial
EC transport and identity-field declarations, not a complete exact-model
Stop/Automatic contract.** No runtime capability or EC-version rule changes.

No vendor executable, EFI program, ACPI method, driver or hardware interface
was executed. Ghidra, Capstone and iASL analyzed inert extracted files only.
All vendor bytes and detailed outputs remain ignored under
`artifacts/model-expansion/missing-model-host/`; none is a distributable asset.
RZ09-0351 has no attributable package from the preceding review. There was no
new concrete package lead here, so no substitute board image was inspected.

## Exact BIOS binding and static ACPI scope

The previously pinned official packages remain the inputs:
[D3 1.03 / RZ09-0300](https://dl.razerzone.com/drivers/Razer%20Blade%2015%22%20Mid%202019%20-%20Base/RazerUpdater_v1.1.1.2_D3-BIOS0103.exe),
[C3 1.04 / RZ09-0301](https://dl.razerzone.com/drivers/Razer%20Blade%2015%22%20Mid%202019/RazerUpdater_v1.1.1.2_C3-BIOS0104.exe),
[DA550 1.06 / RZ09-0328](https://dl.razerzone.com/drivers/Razer%20Blade%2015%E2%80%9D%20Base%20%282020%29%20%7C%20RZ09-0328x/RazerUpdater_v1.1.1.2_DA550-BIOSv0106.exe),
[PI410/PI411 1.08 / RZ09-0370](https://dl.razerzone.com/drivers/Razer%20Blade%2014%22%20%282021%29%20%7C%20RZ09-0370/PI410_PI411-BIOSv0108_v3.exe),
[PI490 2.00 / RZ09-0482](https://dl.razerzone.com/drivers/Razer%20Blade%2014%E2%80%9D%20%282023%29%20%7C%20RZ09-0482x/RazerUpdater_v1.1.1.3_PI490-BIOSv02.00.exe), and
[PI4100 1.06 / RZ09-0508](https://dl.razerzone.com/drivers/Razer%20Blade%2014%E2%80%9D%20%282024%29%20%7C%20RZ09-0508/RazerUpdater_v1.1.1.3_PI4100-BIOSv01.06.exe).
Carrier hashes, signatures, exact packaged SSIDs and ROM hashes are recorded
in the two preceding reviews and were reused without downloading again.

`collect.py` carved bounded DSDT/SSDT declarations with valid ACPI lengths and
checksums from the complete flat UEFI bodies, deduplicating within each model.
There were 64, 65, 73, 48, 62 and 62 unique tables respectively: 374 total.
All 374 disassembled successfully with offline iASL 20260408, SHA-256
`739c597bcee4563f18d13e73b2051bb661707713e6154cf5712a1b7eef4a5df9`.
These are static firmware tables; their presence does not prove Windows
actually loads every conditional table. Dynamically synthesized or modified
tables are outside this extraction result.

The table below pins the DSDT bytes. Method offsets are the AML `MethodOp`
byte within that checksum-valid table, not a physical address or EC address.

| Model | DSDT SHA-256 | Bytes | EC0 `RECR` / `RECW` AML offsets |
|---|---|---:|---|
| 0300 | `fe570797eb8d3640f1cfa5b439ac3d90dcc24a27caf8e09df5f6e1a1a6a14409` | 256,556 | `0x3E61C` / `0x3E663` |
| 0301 | `26d855c14264195da7fdfc629dea2aea05969e7d35183caa1c65f2d1c6019c1f` | 256,964 | `0x3E7B4` / `0x3E7FB` |
| 0328 | `47aa8c1fdd8af6bb4f995b836207686afbea880705fa188fc4b9c3b67d80ef7c` | 281,717 | `0x4489F` / `0x448E6` |
| 0370 | `296ec1908f17a4adee17a2b14ee1031b5c6878b11c811ed05ce39852ae4a044f` | 29,578 | `0x6842` / `0x6889` |
| 0482 | `57806abab4ff61ee7f22c49689f93e3b683e64d443ad74310aa01affec0ae6fc` | 34,393 | `0x6EB0` / `0x6EF7` |
| 0508 | `aef4586de0256e3e729b90eec55900ee3c5963ddd7e91c5b4dad02a5ab1b102e` | 34,063 | `0x6D66` / `0x6DAD` |

## Real host transport, incomplete EC meaning

Each exact DSDT declares `EC0` as `PNP0C09`, resource ports `0x62`/`0x66`,
and `ERAM` as `EmbeddedControl` space at zero, length `0xFF`. The Intel models
place it under `\_SB.PCI0.LPCB`; the AMD models use `\_SB.PCI0.SBRG`.
Each declares 32-bit `ECVR` at EC offset `0x20` and 8-bit `BDID` at `0x28`.
This identifies where the BIOS expects an identity field, but supplies neither
its encoding nor an actual EC version value or approved firmware hash.

All six EC0 implementations also declare `ECMD`, `ECAD`, `ECDT` at
`0xF8`, `0xF9`, `0xFA`. Their serialized `RECR(address)` acquires `ECMT`,
checks `ECON`, writes `ECAD`, writes command `0x80`, waits for `ECMD == 0`,
then returns `ECDT`. `RECW(value,address)` stages `ECDT` and `ECAD`, writes
`0x81`, and waits for the same clearing condition. The mutex acquisition has
a timeout argument, but the subsequent mailbox loops have no finite timeout
and the acquisition result does not gate entry. These AML methods are evidence
of a secondary RAM transport; they are not a safe product adapter.

The AMD DSDTs additionally declare one byte called `PWMV` at `0xAD`. In the
extracted static table corpus its name has no use beyond that declaration.
There is no demonstrated channel identity, scale, actual-versus-requested
meaning, tach conversion, zero-duty branch or Auto handback attached to it.
Neither `PWMV` nor the generic mailbox authorizes a guessed fan write.

## Fan-looking APIs resolve to templates or missing OEM hooks

The Intel DPTF SSDTs contain `TFN1._FPS`, `_FSL` and `_FST`. For example,
0300/0301 table SHA-256 prefix `bc0f562c71f4` has a zero-valued performance
state and `_FSL` calls to `H_EC.ECWT` followed by `H_EC.ECMD(0x1A)`;
`_FST` refers to `H_EC.PENV` and `H_EC.CFSP`. Following those definitions
changes the interpretation: the DSDT's **different** `H_EC` device has
`_STA` returning zero, `ECAV` initialized to zero, `ECRD` returning zero,
and empty `ECWT`/`ECMD` bodies. The extracted corpus contains no assignment
that enables `H_EC.ECAV`. The DA550 DPTF tables resolve to the same sort of
stub device. These calls do not establish real all-fan control or readback.

The AMD PMF SSDTs on 0482 and 0508 expose `PMF7`, which extracts a fan mode
byte and table-index dword and invokes `\_SB.OPM7` only if `CondRefOf` finds
it. No `OPM7` method definition was found in their extracted checksum-valid
tables. Their separate `APX7` debug method writes a trace to port `0x80` and
sets the returned `ECFanTableIndex` and `ECFanRPM` fields to `0xFFFFFFFF`;
it does not read a tachometer. On 0508 these are table prefixes
`2f22cde4e002` and `f071e25ca643`; on 0482 they are `f5f25ec1a4b2` and
`a746ad903589`. Missing dynamically supplied OEM hooks are not ruled out.

## EC init and reflasher code, beyond the label

All three AMD `AmdCpmEcInitDxe` PE modules have entry at image address
`0x2C0`, which registers the event callback at `0x340`. That event callback
locates an AMD protocol, builds context dwords `0`, `0x3930305A` (`Z009`) and
`0x47524253` (`SBRG`), then makes a table-processing call through protocol
offset `0x3D8` on 0370 or `0x418` on 0482/0508, passing GUID bytes at
image address `0x400` and the separate table mutation callback at `0x310`.
This is ACPI-table plumbing, not an embedded EC image or a version-selection
routine established here. The table mutation callback confirms the narrow job:
at file/image `0x310` it compares the dwords for `LPC0`, `M042` and `EC0_`,
and replaces a match only when the
corresponding context dword is nonzero. In this invocation `LPC0` becomes
`SBRG`, `M042` becomes `Z009`, and the zero context leaves `EC0_` unchanged.
The GUID at `0x400` is `11EF89E3-AF8F-4653-805C-90C59C6EAC26`, which
independently resolves to the extracted `CPMEC` SSDT. Its `M040(address)` and
`M041(address,value)` access a one-byte `EmbeddedControl` OperationRegion;
`M31A`/`M319` perform generic one-byte SystemIO access, and `M31B` stages an
I/O write after polling a status bit. This closes the DXE-to-table reference:
the selected payload is host AML implementing general access, not EC machine
code or a fan-specific contract. The protocol implementation that installs
or rewrites the table remains a separate dependency.
The tables are individually pinned: 0370 `72092a755b60...` (1,615 bytes),
0482 `5a905c7917b4...` (1,906 bytes), 0508 `4b7ff2d007a1...` (1,914 bytes).
Their access guards differ: 0370 requires the mutex reference; 0482/0508
add an existing `EC0.OKEC == 1` requirement before the mutex check. These
guards and unresolved dynamically supplied namespace objects cannot be
discarded when interpreting the host interface.

Their `AmdCpmEcInitPeim` images are **x86-32 TE host code**, not EC firmware.
Capstone traces the configuration lookup keyed by `0x30334124`, tests flags
at context offset `0x6D`, and conditionally calls the located protocol with
parameters `0xEA`, `0xEB`, `0xEC`, `0xEF`, `0xED` (and `0xA2`) and an
initial `0x60` parameter. Example 0482 TE file offsets `0x11F`/`0x129`
stage `0xEA` and call protocol offset `0x168`; 0370 uses offset `0x154`.
This review does not name those indirect parameters as proven fan registers.
The code is configuration-dependent EC initialization, with no complete
Stop/readback/Automatic meaning established. Ghidra's PE loader rejected the
three TE images; the separate correctly selected x86-32 disassembly is retained.

`ReFlash`'s `EC FIRMWARE Update` is a standard HII string block, not a payload
locator. Its exact PE-file string offsets are `0xE6EA` on 0300/0301,
`0x7B0A` on 0328, `0x77AA` on 0370 and `0x9B48` on 0482/0508.
On 0300/0301 the HII string is token 10 and is referenced by an IFR action at
`0x939E`, question ID `0x2002`. Tracing the actual callback at `0x6AA4`
reaches a filename buffer and helper `0x7228`, which locates a file-system
path and opens/closes a file; that path supplies no embedded EC hash, version,
fan ingress or fan completion semantics. The two Intel modules are byte-identical
(SHA-256 `e3eb2c562e7eec5ae0dcb375867e02c13b9ea3c33ffb886701cfa430072cf427`).
The 0482/0508 ReFlash modules are also byte-identical, independently of their
different model binding. Neither identity licenses sharing EC contracts.

## Checks and next missing fact

Artifacts include input/body/table manifests with SHA-256 and source offsets,
all 374 iASL logs and DSL outputs, exact AML declaration offsets, a semantic
field-offset report, bounded HII parsing results, 21 imported PE-module
decompilations and selected callback decompilations, plus three TE disassemblies.
The parsers and Ghidra scripts are retained for reproduction. Static checks
covered ACPI length/checksum closure, method opcode/name offsets, declared field
bit widths, HII package bounds, and correct x86 host machine selection.

The next useful fact is the **EC-side implementation behind this exact
model's mailbox**, including its `ECVR` encoding and a source-bound firmware
identity, or a complete implemented OEM fan hook with independent actual PWM
and tach readback for every channel and exact Automatic restoration. For 0351
the attributable package itself is still missing. This result narrows where to
look; it neither proves universal absence nor admits a new command, EC version,
hardware test or runtime backend.
