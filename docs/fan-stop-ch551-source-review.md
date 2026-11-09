# RZ09-0330 direct-duty source review

Reviewed 2026-11-08 against core `71193867c99dcabde57fd1a56322eae75d072f05`.
The exact CH551 EC 1.01 image and packaged flasher establish the two-fan
host-mailbox direct-duty and Automatic handback contract for RZ09-0330,
Blade 15 Advanced (2020). The carrier also names Studio variants; runtime
support remains limited to the already admitted exact RZ09-0330 host/HID.
**Physical stop and recovery are untested.**
The source contract can share the guarded two-channel primitive, subject to
fresh exact identity, version, power and thermal admission. Ordinary HID fan
capabilities do not describe this separate EC route.

The [offline profile](../tools/firmware/profiles/ch551-ec-1.01.json) pins the
carrier/image and annotates 13 of 2,048 decoded descriptors. It exposes no live
control API. No vendor executable or assembly was executed; no hardware,
driver installation, ACPI evaluation or live setting was accessed. Full static
artifacts remain ignored under `artifacts/model-expansion/intel0330`.

## Exact inputs and identity

The [official EC article](https://mysupport.razer.com/app/answers/detail/a_id/2827)
lists RZ09-03304 and RZ09-0330Q. Fresh download size/hash matched the existing
[2019–2020 catalog](../evidence/source-reviewed-blades/2026-11-07-models-2019-2020.json)
before extraction. Authenticode was Valid for Razer USA Ltd. The EC payload's
size/hash matched that catalog before disassembly.

| Input | Bytes | SHA-256 |
|---|---:|---|
| [RazerUpdater_v1.1.1.2_CH551-ECv0101.exe](https://dl.razerzone.com/drivers/Razer%20Blade%2015%22%20Advanced%20%282020%29%20/RazerUpdater_v1.1.1.2_CH551-ECv0101.exe) | 598,032 | `3ba19f72e3a53d88d2a8d1c2aa5c6e9a4b907e85e61a0b2803d5b9ca6cb78d44` |
| `CH551_EC_01_01.bin` | 262,144 | `0487bd1357b6ac6c2c18b8e514b6a1098cb8e198b534e588045770a8c7140689` |
| Main updater, member 3 | 349,616 | `60eebbfee17646d951f2287e22c67c82db4df5d9f37e1741488d3f578723c07e` |
| `Razer_EC_Flash_v1.03.exe`, member 7 | 46,592 | `3590f72531078eb9562f8ed864753ea710a1d2723f693923b99b3a981b78f07b` |

EC member 6 has stored-length offset `3D4E2`; flasher member 7 is at `4C48B`.
Release metadata explicitly names SKU RZ09-0330, EC `01.01`, and SSIDs
`1A582009/1A58200A`. The catalog independently pins Synapse Product 595,
`1532:0253`, and its existing typed `07/8C` actual/recommended adapter query.
No new power query is proposed.

Main updater `MainWindow.GetECVersion`, token `0600003A`, RVA `2CB8`, reads
`HKEY_LOCAL_MACHINE\Hardware\Description\System\BIOS` values
`ECFirmwareMajorRelease/ECFirmwareMinorRelease` at IL `0000..0045`.
This is the vendor's source for its version comparison and supports the
existing reported-EC-version admission path. It is not live hash attestation.
A missing, unknown or different version must fail closed. The earlier catalog's
separate CPU Boost prerequisite limitation is not changed by this review.

**Do not reinterpret bank-zero E0/E1 as firmware version.** They map
`803A0/803A1`; function `9A58`, including store `9A78`, writes an ADC-derived
word there. Bank-zero `9B` maps `81329`, also not established as EC version.
No new identity-register command is proposed. Exact carrier identity does not
prove that an arbitrary live device is running these bytes.

## Transport and completion

The EC is Andes NDS32, base zero: instruction addresses below are file offsets.
Bank-zero descriptors begin at `25170`; eight bank pointers begin at `289AC`.
Startup `50A4..50CA` copies 60 bytes from `289A4` to `81000`, placing those
pointers at `81008`. Exact p-code for `2386` adds `1008` to GP (`80000`), and `238A` loads the bank pointer from that array. Each descriptor is four-byte little-endian metadata then
a four-byte address. Reviewed writable fields have metadata 2; read-only
fields have metadata 1.

Static PEReader metadata/IL from the exact packaged flasher establishes:

- `RWIo.WriteRzEcMbox`, token `0600003D`, RVA `4639`, writes index to port
  `300` then data to `301`. `ReadRzEcMbox`, token `0600003E`, RVA `4668`,
  writes the index to `300`, then reads `301`.
- `RWIoI2C.I2C_Read_Write`, token `0600005F`, RVA `4EB4`, uses address `58`,
  bank `90`, index `91`, count `92`, payload `A0` onward and command `93`.
  Read publishes `A0` last at IL `005A..0064`, then polls zero at
  `006E..007F`. Write stages payload before publishing `A1` at
  `0279..0283`, then polls zero at `028D..029E`.
- Final IL `03D4..03E5` checks status `94==80`. Vendor polling itself does
  not provide bounded transactions or exclusive ownership.

EC handler `2198` consumes `F02290..F02294` and `F022A0`. Bounds
`2374..2384` require bank below eight and count at most 32. It selects the
bank pointer at `2386..238A`, then writes success status `80` at `2392`
**before copying**. A0 read loop `23B8..23D8` wraps the index to eight bits;
A1 write loop `240A..2442` skips metadata 1 at `242A`, writes at `2432`,
and marks the mapped-change flag `8000E` at `2436`. Errors are bank `84`,
count `82`, command `83`. Read-only skips may still return status 80.

Caller `FA3C` calls the handler at `FA3E`, then clears command `F02293` at
`FA48` after return. Require command zero, success status, descriptor
consistency and an independently published fresh readback. Staged payload,
status alone, or an unrelated cached reply cannot confirm a control changed.
Mailbox payload offset A0 is distinct from bank-zero register A0.

## Exact register and fan contract

| Bank-zero index | Backing location | Meaning / access |
|---|---|---|
| `06` | `802E3` | Active bit 0; read-only |
| `08/09` | `80816/80817` | Measured speed, 100 RPM/count; read-only |
| `0A/0B` | `80940/80941` | Policy command/status; writable/read-only |
| `1A` | `8035F` | Normal `0..7`, direct `FE`, target `FF`; writable |
| `36` | `8034D` | Manual flags, bit 0 manual handling; writable |
| `B0/B1` | `F01807/F01803` | Actual PWM bytes; read-only |
| `B2` | `F01841` | Shared PWM cycle byte; read-only |
| `B8/B9` | `80394/80395` | Staged raw duty bytes; writable |
| `F0` | `80425` | Override flags, including permission bit 6; writable |

All 13 descriptors were asserted directly against this exact image.
Policy `13E52` gates on active bit 0 at `13E56..13E60`, separates FE/FF
from the normal selector range at `13E72..13E8C`, and tests F0.6 at
`14318..14320`. FE stores `1435E/1438E` copy B8/B9 unchanged to actual
PWM, including zero. Equality skips an already-equal store. Permission absent
sets selector zero at `14328`, which is not exact restoration of an arbitrary
captured normal profile. Normal feedback also writes zero at `13342`.

Cycle initialization `124DE..124E6` writes B7 (183); runtime must read the
live cycle and bound requests against it. These bytes are raw counts, not
percentages. Tach acquisition has multiple paths: reviewed publication divides
measured speed by 100 at `F5B2/F5B6`, `1283E`, `1298A/1298E` and `12C0E`.
Staged zero, actual PWM zero, coarse tachometer zero and mechanical stop remain
separate observations. No temperature scale is inferred from these 13 fields.

Generic FE additionally copies `8038E/8038F` to `F01808/F01809` at
`143BC/143E0`. This image's seeder `12C20..12C48` selects four factory
regions `3B000/3C000/3D000/3E000`, each with eight 512-byte records. All
32 record headers encode two fans. Loader `13A80..13A84` publishes header
low-three-bit count; decoder `12E84..12EAA` consumes two targets and zeros
the unused target words. Extra generic peripheral slots do not establish
additional physical fans. No new control of them is proposed. The offline
profile imports no thermal curves or other model's manual-extension layout.

F0 writes replace the whole byte. Bit 5 suppresses sensor paths at `D554`,
`E628`, `F892` and `1046A`; bits 1/2 affect state handling at `9C60/9D0E`;
bit 0 selects another PWM path at `AB36`. Bit 6 also skips refresh of
`80493` at `EED4..EF64`, before the common minimum of policy contributors
is published to `8033C` at `EFAC`. It is not an isolated fan-only bit or a
uniform freeze of all policy contributors. Preserve other bits and keep the
existing bounded ownership and thermal/power gates.

Baseline must have normal selector, manual zero, policy idle, override bits
5/6 clear, active state, valid cycle and bounded requested/actual duties.
Stage B8/B9, set only permission bit 6, then publish FE last.

## Auto handback and runtime boundary

Supervisor `13944` checks active state and excludes normal feedback `13292`
only for FE at `13988..13990`. That normal-feedback dispatch does not require
permission clear. Restore the captured normal selector first; independently
observe selector/manual zero/policy idle, then restore the owned override byte
and inactive staging bytes. Reobserve every owned control.

FE retains last-normal selector `8034F` and clears stage bookkeeping.
Reload depends on previous selector, refresh/manual state and `8044E` at
`1441C`; `14608` can clear manual flags and `14614` records the last normal
selector. Policy status 4 at `ACD0` follows evaluator call `ACBE` and is not
proof of Auto, settled speed or safe cooling by itself.

The shared mailbox has no established universal lock, firmware lease or
crash-proof Auto handback. Preserve process ownership, Synapse exclusion,
fresh identity/version/power/thermal gates, independent readback, journal,
watchdog and guardian recovery. This review supports exact RZ09-0330/PID0253
with reported EC 1.01 under those gates; it does not admit arbitrary CH551
images, other models, CPU Boost or new generic firmware commands.

The independently established delta from the shared two-channel contract is
image identity and instruction/table locations. Register meanings and
selector-first restoration match. Source support is complete; physical fan
stop, restart, contention and recovery remain untested.

Static listing `ec-disassembly.txt` SHA-256:
`502e27621034bfbcd59eb79ac06b5d29b842147461bcccf8d0160ddc1ada867d`.
Flasher metadata/IL `host/block-07-metadata-il.txt` SHA-256:
`654f35191a418ffb9f65eb8c4ee5746e324391456628eec5feedc0270177e0d0`.
The exact carrier passed the offline inspector: 2,048 descriptors, 13 annotated
fields and zero warnings.
