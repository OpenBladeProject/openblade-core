# Blade 16 BIOS and EC source cross-check

Reviewed 2026-11-07 against core commit
`d49eb99a6175291fbbf5abfa1a0fac1a3e93b08e`.
The [sanitized evidence record](../evidence/firmware-source-review/2026-11-07-blade16.json)
contains the complete carrier/member SHA-256 values, extraction offsets and
32 derived factory fan-table summaries. No vendor binary is committed.

This is a bounded static review of exact RZ09-0581 and RZ09-0528 firmware,
not a new physical validation or a complete review of every firmware command.
Existing runtime admission and per-feature physical evidence remain authoritative.
No updater, extracted firmware, ACPI method or firmware helper was executed;
no device access, installation, flashing, firmware mutation or sleep test occurred.

## Inputs and method

| Model | Component | Input | Status |
| --- | --- | --- | --- |
| RZ09-0581 / `1532:02E0` | BIOS 4.01 | `SO6120_BIOSv0401.exe` | Existing local carrier; Razer USA Ltd. Authenticode Valid |
| RZ09-0581 / `1532:02E0` | EC 1.04 | `SO6120_ECv01.04.exe` | Existing local carrier; Razer USA Ltd. Authenticode Valid |
| RZ09-0528 / `1532:02C6` | BIOS 2.02 | `SO6110_BIOSv0202.exe` | Downloaded from the exact official article; Razer USA Ltd. Authenticode Valid |
| RZ09-0528 / `1532:02C6` | EC 1.09 | `SO6110-ECv0109.exe` | Downloaded from the exact official article; Razer USA Ltd. Authenticode Valid |

The official model indexes are [RZ09-0581 downloads](https://mysupport.razer.com/app/answers/detail/a_id/20854)
and [RZ09-0528 downloads](https://mysupport.razer.com/app/answers/detail/a_id/19821).
The 0581 component articles are [BIOS](https://mysupport.razer.com/app/answers/detail/a_id/20847)
and [EC](https://mysupport.razer.com/app/answers/detail/a_id/20850).
The 0528 [BIOS article](https://mysupport.razer.com/app/answers/detail/a_id/15235)
links `https://rzr.to/2vxhvx`; its [EC article](https://mysupport.razer.com/app/answers/detail/a_id/21226)
links `https://rzr.to/58kmn2`. Resolved official CDN URLs are pinned in the evidence.
This review does not claim a fresh download comparison for the retained 0581 carriers.

NSIS data blocks were decompressed offline with standard-library LZMA.
A carrier block offset names the four-byte stored-length field, before the
compressed member. BIOS data was parsed with UEFIExtract NE alpha 74 and
ACPI tables checked for bounded declared length and an eight-bit zero checksum.
ACPICA iASL 20250807 disassembled the selected tables without evaluating them.
The retained 0581 BIOS extraction first reconstructs each AMI PFAT component;
parsing its interrupted compressed stream as one ordinary volume is insufficient.
Ghidra 12.1.3 recovered selected code; pseudocode is an analysis aid, not vendor C.

## RZ09-0581: normal thermal policy corroborated

EC image `SO6120_EC_0104.bin` is 262,144 bytes,
SHA-256 `8537e9fd55b7b35504d8d87b0191acc4391b67365b3fe72f6e1029a424c9e707`.
Its identity at image offset `0x90` is `ITE8320EC-V01.04`.
The retained NDS32 instruction listing establishes these selected paths:

- `0x15E46` handles normal selectors `0..7`, separately from internal `FE/FF` paths.
- The ordinary manual clamp at `0x15C62..0x15D9C` multiplies requested bytes by
  100 RPM. At `0x15C88..0x15C9E`, selector 3 alone maps below-minimum requests
  to zero; other normal selectors use the minimum.
- Loader instructions `0x16406..0x16418` and `0x16432..0x16444` read minimum
  and maximum fields at table-extension offsets `+0x21` and `+0x26`.
- A fresh independent decode checked four board groups at `0x3B000`,
  `0x3C000`, `0x3D000`, `0x3E000`, each with eight 512-byte records.
  All 32 records (344 stages) contain a first-stage target of 0/0 RPM and
  ordinary manual minimum/maximum fields of 2,000/5,400 RPM.
  Selector 5 records are at `0x3BA00`, `0x3CA00`, `0x3DA00`, `0x3EA00`.

Human-readable mode names come from the separately reviewed exact-product
protocol. The firmware records contain numeric selectors. These findings
corroborate ordinary mode-specific fan policy and explain why retaining a low
requested manual value does not establish the same effective fan target.
They do not justify changing the requested-value contract or adding new modes.

The factory table is not a read of the active persistent table. Valid persistent
records can replace defaults. Multiple input channels, hysteresis, dwell and
other state gates affect stage selection. In particular, `0x15030..0x15068`
can raise decoded targets to at least 3,000 RPM when either of two separate
state flags is set. A zero target is not measured fan rotation.
Internal duty branches and generic EC mailboxes do not establish a supported,
bounded host control with safe ownership and exact restoration.

## Both models: thermal-zone name does not prove CPU temperature

The reviewed DSDTs contain these exact names. At the reviewed baseline,
`SafeWindowsFanTemperatureSource.WindowsThermalZone.RazerEcCounterPaths`
(`src/OpenBlade.Service/FanCurveControl.cs:394-400`) listed both paths:

| Model | Thermal zone | `_TMP` name offset within AML |
| --- | --- | --- |
| RZ09-0581 BIOS 4.01 | `\_SB.PC00.LPCB.EC0.TZRZ` | `0x370AA` |
| RZ09-0528 BIOS 2.02 | `\_SB.PCI0.SBRG.EC0.TZRZ` | `0x91BB` |

Both ordinary ACPI `_TMP` methods return the literal `0x0C6E`:
3182 tenths kelvin, approximately 45.05 degrees Celsius. The same literal is
present in the retained live 0581 DSDT. The retained live table and BIOS image
table have different hashes; they are not represented as byte-identical tables.
A complete dynamic CPU-temperature source is therefore **not established by
these thermal-zone definitions**. A counter's path or plausible numeric value
alone cannot establish the sensor's meaning or freshness.

The 0528 DSDT is the checksum-valid 40,255-byte raw section under FFS GUID
`C118F50D-391D-45F4-B3D3-11BC931AA56D`, SHA-256
`aa91c2da04dd656016958e785ed6f1e2f41ecd222e5d7cca1e906fcc775e6521`.
It declares `CPUT`, `CP2T`, `GPUT` at EC offsets `0x56`, `0x57`, `0x58`, and
a 32-bit `ECVR` field at `0x20`. Those declarations do not independently
establish sensor calibration, a supported host acquisition route or live values.
Its `_TZP` returns `0x1E`, and its critical/hot trip-point expressions are
separate from `_TMP`.

The 0528 `RazerAcpiDxe` member (FFS
`48E0B361-F517-41E2-870C-73C84475B52C`, 7,680 bytes) was also inspected.
Recovered entry RVA `0x17EC` uses helper `0x1B50` to request EC data selectors
`E8/D0` and populate NVS. This bounded review did not establish a dynamic CPU
getter or an `_TMP` repair from that code. It is not an exhaustive absence proof
for every boot/runtime patch or Windows counter implementation.
The associated software fix removes both paths from the shared CPU-temperature
reader. No replacement sensor or numeric 45-degree blacklist is introduced.
When CPU temperature is unavailable, the existing custom fan-curve calculator
selects the model's admitted curve maximum: 5,400/5,400 RPM on RZ09-0581
and 5,000/5,000 RPM on RZ09-0528, including when the GPU is inactive. Automatic firmware fan handling
is a separate path. Focused software tests cover null CPU readings and that
existing fallback; no live PDH sample or physical fan response was tested here.

## RZ09-0528: exact firmware and recovered EC control paths

BIOS `SO6110_202.rom` is 33,554,432 bytes, SHA-256
`5bbd09cda602e8555b04d7ca2a3a7bcbbdfc929be8a7bc5c81e6c3dfc7cbb2f4`.
The static flash-script name and updater configuration bind it to SSID
`1A58300E`, family `RZ09-0528`.
EC `SO6110_EC_0109.bin` is 262,144 bytes, SHA-256
`44dc4e4c92ea4be9def93670a95b2bf859cb648b8dfe393fb5877735debbef5d`.
Its header at `0x90` names `RTW--81202-01.09`; `0x2D00` names
`RTW81202 PI480`. The updater declares SSID `1A58300E` and five cases,
`RZ09-05285` through `RZ09-05289`.
These are updater provenance, not new runtime SKU admission restrictions.

### EC 1.09 follow-up: dispatch to state and policy

The follow-up began from core commit
`96a8f82ed69f1e98dbc9f404aee03c535a9708c8` recovered coherent RISC-V RV32
with compressed instructions. Runtime code addresses below use the
`0x80000000` flash mapping; subtract that base to obtain file offsets.
Reset offset `0` branches to `0xA0`; offset `4` branches to interrupt entry
`0x400`. Startup `0x8000013C..0x80000164` copies 36 bytes from flash
`0x80023D16` to `0x80081000`. These bytes contain eight bank pointers,
starting with `0x800232A8` and decreasing by `0x800` per bank. The code
uses both low `0x80000` and high `0x80080000` RAM ranges. These operand
ranges are not an independently verified statement about physical aliasing.
No 0581 instruction or table offset was transferred to this analysis.

Two actual EC ingress paths reach the same mapped fields:

- SMBus initialization `0x80009D7E` calls `0x8001CA1A` with slave-address
  register value `0x58`. Interrupt dispatcher `0x80003200` indexes the table
  at `0x80005000`. Slot 9 (file offset `0x5024`) names `0x800189BE`, whose
  call at `0x800189C2` reaches handler `0x80001958`.
  `0x80001A64..0x80001A94` loads the selected bank's descriptor and target.
  The actual mapped write is `0x80001B00`; descriptor metadata byte `1`
  prevents it. A separate narrow manual-bit-7/index exclusion also exists.
- The periodic call at `0x8000ECB0` reaches mailbox handler `0x80014F1C`.
  MMIO `F02290..F02294` contains bank/index/length/command/status;
  the data buffer starts at `F022A0`. `0x800151C8..0x800151F0` bounds bank
  to 0..7 and length to 0..32. Commands `A0/A1` read/write mapped bytes;
  index addition wraps to eight bits. `0x80015292` skips metadata `1`;
  `0x8001529A` stores the data byte and `0x8001529E` sets a change flag.
  Status `0x80` is set before the loop, so a skipped read-only field can
  still have that transfer status. It is not proof of a feature change.

The descriptor is eight bytes: little-endian 32-bit metadata followed by a
32-bit pointer. For bank zero, its file offset is `0x232A8 + index*8`.

| Bank-zero index | Target | Recovered consumer |
| --- | --- | --- |
| `00..03` | `84E20..84E23` | Read-only version bytes |
| `08/09` | `80816/80817` | Distinct fan-speed fields, separate from requests |
| `1A` | `8035F` | Normal mode selector |
| `1B/1C` | `80330/80331` | Requested fan target bytes |
| `36` | `8034D` | Manual-state flags, including bit 0 |
| `2A/2B` | `808E0/808E1` | Distinct Custom performance selectors |
| `C1` | `808E4` | Shared power-control flags byte |
| `C2` | `808EE` | Protection-enable bit 7 and low-seven-bit ceiling |

All listed control descriptors carry metadata `2`; the handler's actual
exclusion is metadata equal to `1`, rather than a typed feature contract.
These generic bridges are not proposed as production or developer commands.

### Normal thermal modes and factory fan records

Policy `0x8000915E` is gated by `RAM[84EE3]` bit 0. Instructions
`0x8000918A..0x8000919C` separate internal `FE/FF`, then bound normal
selectors to 0..7. Loader `0x800089EA` obtains record `0x20 + selector`,
with persistent metadata/checksum checking and factory seeding when necessary.
Seeder `0x8000781A` selects the board group through `RAM[804E9]` and bases
`3B000`, `3C000`, `3D000`, `3E000`.

An independent decoder checked all 32 records and 344 stages. All four groups
contain eight 512-byte records, two fan targets, a sensor mask of `BF`,
21-byte stage rows and a first-stage target of 0/0 RPM. Stage decoder
`0x80007A56` multiplies target bytes by 50 RPM.

This exact image has a different extension layout from 0581:
`0x80009092..0x800090A4` loads extension `+4` into `80555` (minimum), and
`0x800090BE..0x800090D0` loads extension `+9` into `8055A` (maximum).
Every factory record contains minimum 20 and maximum 51: 2,000 and 5,100 RPM
in ordinary manual-target calculation. The existing OpenBlade 5,000-RPM
0528 cap remains a separate, more restrictive admitted product contract.
These image defaults do not expand that cap or establish effective fan RPM.

Normal manual handler `0x80008DFC` requires `8034D` bit 0 and per-channel
gates. The first fan path `0x80008E18..0x80008E5C` loads request `80330`,
clamps it against `80555/8055A`, treats a below-minimum request as zero only
for selector 3, multiplies by 100, and writes the target word `80500`.
The second fan path `0x80008E6E..0x80008EB6` does the same for `80331/80502`.
Targets, active table selection, firmware protections and measured rotation
remain distinct. No active table or live tachometer was read.

### Custom performance, charging, power flags and version

In mode 4, `0x800145A6` consumes the two selectors at `808E0/808E1`.
The first selects a policy index and copies a 19-byte record from
`86D00 + index*0x13` to `86FE8` at `0x80014648..0x8001465E`.
The second feeds the distinct computation at `0x80009E34`, with separate
0/1/other branches and board/power gates that update GPU request/configuration
fields. CPU/GPU names remain anchored in the separately reviewed host vendor
contract; these exact EC paths establish distinct consumers. They do not
establish effective watts, maximum frequencies or GPU TGP.

Charging handler `0x8000C9AC` consumes `808EE`: bit 7 enables a low-seven-bit
ceiling, while the adjacent byte `808EF` is an inhibit latch. Under the
existing charger gates, `0x8000CC06..0x8000CC86` sets that latch when the
battery state-of-charge field `84E80` reaches the ceiling and clears it below
ceiling minus five (or for a ceiling no greater than five).
The adjacent firmware battery diagnostic identifies this field as `Rsoc`.
When enabled and latched, `0x8000CC4A..0x8000CC5A` writes zero to the charge
current-request field `84E3C` and sets `84EA2` bit 3. Power-control flag bit 2
bypasses this inhibit at `0x8000CC40..0x8000CC48`, corroborating its temporary
full-charge role. The handler copies configuration into `86055` and
`8605A` bit 0 and marks it dirty; complete durable storage/restoration was
not reviewed. No physical charging response or admitted ceiling range/step
is established by these internal comparisons.

The mapped `C1` write replaces the complete `808E4` byte. Bit 1 also feeds
Custom-mode fan handling at `0x8001447E`, including an internal 5,500-RPM
target literal under its existing gates, and changes GPU policy branches.
This corroborates that the byte contains independent settings and requires
preservation of unrelated bits. It does not establish an effective tachometer
value or authorize a new host range. Meanings of CPU-boost bit 0 and
local-dimming bit 3 were not independently completed from this EC review.

Initializer `0x8000BBB4` writes ASCII `0109` into `80A82..80A85`.
Publisher `0x8000E0D2`, specifically `0x8000E130..0x8000E18A`, copies them
to `84E20..84E23` and MMIO `F02286..F02289`. Bank-zero indices `00..03`
expose the former through read-only descriptors. This establishes a
firmware-owned version representation, not the identity or version of a
running laptop or equivalence to Windows registry metadata.

### Reproduction and remaining boundary

The ignored analysis directory `artifacts/firmware-model-review/ec0528`
contains the pinned-image decoder `decode_ec0528.py`, a complete derived
factory-table/map JSON, Ghidra load-map script and selected instruction
listing. The committed evidence record contains their source offsets, bank
pointers, selected descriptor records, per-table hashes and call/dataflow
pointers without vendor binaries. Reproduction requires the pinned official
image: decode eight little-endian pointers at `23D16`, each eight-byte bank
record, and 32 fixed-size factory records at the code-selected bases. For
this image, assert 32 records, 344 stages, extension `1A8`, and the per-model
`+4/+9` manual fields. Preserve requested targets separately from tachometry.

Compiler save/restore stubs use `JAL t0` and nonstandard returns; automatic
pseudocode contains `extraout` and jump-table artifacts. The concrete
conclusions above were checked against explicit instructions and image data.
A recovered EC SMBus/mailbox chain is still separate from the normal `02C6`
MCU translation of HID `0D/01`, `0D/02`, `07/12` and `07/0F`, which was not
traced here. No generic register writer, new capability or hardware test is
admitted by this review. Keyboard-mode semantics remain outside this trace.

## Result and remaining scope

The images corroborate selected thermal semantics for both models and recover
0528 banked control, Custom-selector, charge-ceiling and version-publication
paths. They also identify a concrete limitation in using
their ordinary ACPI thermal-zone definitions as proof of CPU telemetry.
No additional firmware command, model capability, electrical GPU power-off
claim or physical-validation status follows from this review. Battery,
keyboard, lighting, lifecycle recovery and other controls retain their own
reviewed contracts and evidence. The accompanying shared telemetry fix rejects the two constant-backed paths;
it adds no firmware write or temperature-source admission. No overheating or
physical thermal outcome is inferred from this static finding.
