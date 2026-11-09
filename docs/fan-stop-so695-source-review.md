# RZ09-0483 Lamborghini Stop/Auto source review

The exact SO695 Lamborghini EC 1.01 image independently establishes the
same two-channel host-mailbox, direct-duty and selector-first Auto primitive
used by the guarded workflow. It can be represented as a separate firmware
variant for RZ09-0483 (`1532:029F`), with the existing ownership, thermal,
lifecycle and recovery gates retained. **Physical fan stop and recovery remain
untested.** This review does not extend SO690 evidence to SO695 by similarity.

The [offline profile](../tools/firmware/profiles/so695-lb-ec-1.01.json) is bound
to its own carrier and image hashes. It decodes 2,048 descriptors and annotates
13 control fields plus two read-only identity fields. It includes no thermal
tables or live access.

## Exact inputs and variant identity

The official [Lamborghini updater article](https://mysupport.razer.com/app/answers/detail/a_id/21282)
identifies RZ09-0483x, EC 1.01 and the separate
[SO695 carrier](https://dl.razerzone.com/drivers/Razer%20Blade%2016%22%20%282023%29%20%7C%20RZ09-0483/S9Lamborghini_EC_0101.exe).
The freshly downloaded carrier has a valid Razer USA Ltd. Authenticode signature.

| Input | Bytes | SHA-256 |
|---|---:|---|
| `S9Lamborghini_EC_0101.exe` | 666,472 | `6f6d2240b401ca117293f8565ecbf41458fb7ec0450351af20564f70214c7d84` |
| `SO695_LB_EC_01_01.bin` | 262,144 | `486daab302359085b421c0c68d8092be910930a2ef3912ec39dd8f04c89464b8` |
| Packaged EC flasher, block 6 | 27,648 | `2f205c8120abbbf556dd13d9243f4b5cb25afbf05b943e2e5cd75174513eb2bf` |
| Main updater module, block 3 | 385,224 | `952f90c2e9fad6b1b5d5dfb4eaddc70c15855cad107455a0ec1376eb9c0d3d6a` |

The image is NSIS block 7, stored-length offset `0x44D5D`. Block 8 names the
image and SSID `3009`; block 12 binds SKU `RZ09-0483`, SSID `1A583009`,
EC `01.01` and `Razer_EC_Flash_v2.1.1.exe`. The standard SO690 carrier instead
binds SSID `1A583006` and EC `02.01`.

The Lamborghini article links the same keyboard firmware download as the
[standard 0483 article](https://mysupport.razer.com/app/answers/detail/a_id/14883).
The pinned standard package's keyboard scripts explicitly target `1532:029F`.
The existing [Product 671 adapter-power review](synapse-adapter-wattage.md)
provides the read-only `07/8C` query contract. None of these facts makes the
SO695 EC image interchangeable with an arbitrary standard-model EC image.

Represent SO695 1.01 separately from SO690 2.01 in firmware admission and
recovery provenance. A reported version is a compatibility check, not a live
image hash or board attestation. Preserve that limitation when selecting a
variant: require the read-only EC family discriminator below as well as
model/HID/version. This review does not establish any unreviewed standard
EC 1.01 image's behavior.

Analysis was static: NSIS decompression, PEReader metadata/IL and NDS32
instructions. No vendor payload or assembly was executed, hardware accessed,
or software installed. Raw inputs and derived listings are ignored under
`artifacts/model-expansion/intel0483-lamborghini`. Some unrelated Ghidra
switch/function recovery produced warnings; the relevant explicit instruction
paths below were checked. All EC code addresses below are file offsets.

## Read-only variant discriminator

The exact updater provides a discriminator through the existing mapped `A0`
read path: bank-zero `E3/E4`, little-endian, identifies the reported EC family.
Require `E3=09`, `E4=30` (`0x3009`) together with RZ09-0483 / `1532:029F` and
EC 1.01 for the Lamborghini contract. The reviewed standard SO690 instead
publishes `06/30` (`0x3006`), so its version alone cannot select SO695.

In the Lamborghini image, descriptor file offsets `29E98/29EA0` contain
metadata 1 and backing addresses `803A2/803A3`. Initialization `ACBE` checks
the retained version/family data, then `AD0C/AD0E` loads `09/30` and
`AD2E/AD32` publishes them to those backing bytes. In standard SO690,
`AD14` loads 6 and `AD32/AD36` publishes `06/30` to the same logical fields.
These are firmware-reported identifiers, not proof of physical board straps.

Block-6 `RWIoI2C` static constructor, token `06000070`, RVA `42A5`, initializes
`EC_BANK=0`. `Program.ShowEcVersion`, token `0600002F`, RVA `240C`, reads
seven mapped bytes starting at `E0` in IL `0093..00A0`. IL `00BC..00E1`
assembles `ECIC_SSID = (buffer[4] << 8) | buffer[3]`, which is `E4:E3`.
`Program.Main` IL `01D3..01E6` compares it with parsed `ToolConfig` SSID
`3009` and refuses a mismatch. This is the vendor's own family check.

A runtime implementation can use two existing bounded one-byte mapped reads,
with the existing ownership/completion checks, before selecting the variant.
Reject unreadable, inconsistent or mismatching identity and include the
expected family in guardian/recovery provenance. Do not write these fields,
change the shared SMBus bank selector, or invoke updater routines. The
updater also contains a legacy direct-index SSID reader, but it is unnecessary
for this mapped-field contract. Matching family/version is compatibility
evidence, not live image attestation.

## Host transport and completion

In the exact block-6 flasher:

- `RWIo.WriteRzEcMbox`, token `0600004A`, RVA `37AD`, writes index port `300`
  and then data port `301` (hexadecimal). `ReadRzEcMbox`, token `0600004B`,
  RVA `37C8`, selects the index at `300` and reads `301`.
- `RWIoI2C.I2C_Read_Write`, token `0600006C`, RVA `3E78`, handles address `58`.
  It stages bank/index/count at `90/91/92`, with payload at `A0` onward.
  Read command `93=A0` is published last at IL `005A..0064`; command-zero
  polling is at `006E..007F`. Writes stage the descriptor and payload before
  publishing `93=A1` at `0279..0283`, then poll at `028D..029E`.
- IL `03D4..03E5` reads status `94` and accepts `80`.

EC handler `203C` bounds the mapped bank to `0..7` and count to `0..32` at
`2272..2282`. It selects the descriptor table at `2288`, then stores status
`80` at `2290` **before** copying. The `A0` loop is `22B2..22D2`; the `A1`
loop is `2304..233C`. Descriptor metadata 1 skips a write at `2324`; permitted
writes occur at `232C` and set mapped-change flag `8000E` at `2330`.

Caller `11D24` calls the handler at `11D26` and clears command `F02293` at
`11D30` after it returns. Command-zero, status and descriptor checks followed
by a separate fresh mapped readback are therefore required. Status `80`
alone can accompany an ignored read-only write. These observations do not
establish exclusive access against other port clients.

## Register and entry contract

Reset `50A4..50CA` copies ROM `2C7A4`, length `15C`, to RAM `81000`.
The ROM bank-pointer array at `2C8C4` becomes RAM `81120`, matching the handler.
Bank-zero descriptors begin at ROM `29780`: each is little-endian 32-bit
metadata followed by a 32-bit backing address.

| Index | Metadata | Address | Meaning |
|---|---:|---|---|
| `06` | 1 | `802E3` | Active bit 0 |
| `08/09` | 1 | `80816/80817` | Measured-speed publication, 100 RPM/count |
| `0A` | 2 | `80940` | Separate policy command |
| `0B` | 1 | `80941` | Separate policy status |
| `1A` | 2 | `8035F` | Thermal selector |
| `36` | 2 | `8034D` | Manual flags |
| `B0/B1` | 1 | `F01807/F01803` | Actual PWM duty bytes |
| `B2` | 1 | `F01841` | Shared PWM cycle |
| `B8/B9` | 2 | `80394/80395` | Staged direct-duty bytes |
| `F0` | 2 | `80425` | Whole-byte override flags |

Policy `14D18` requires active bit 0 at `14D1C..14D26`. Selector `FE` branches
at `14D40..14D44` to `15164`, where `F0.6` is required. With permission set,
`15192..151AC` copies `B8` unchanged to `F01807`, and `151C2..151DC` copies
`B9` unchanged to `F01803`. Equality skips an already-applied store; zero has
no minimum clamp or special rejection. This FE branch contains exactly those
two PWM stores, followed by bookkeeping reset and return.

Missing permission selects the factory/default byte at `8600B` through
`1516E..15176`. That is not restoration of an arbitrary saved selector.
Requested-RPM bookkeeping multiplies request bytes `80330/80331` by 100 into
`80500/80502`; it does not read tachometers.

The existing baseline gates apply: captured normal selector, manual flags
zero, idle policy command, active state, override bits 5 and 6 clear, a fresh
nonzero cycle, and requested/actual duties within that cycle. Stage `B8/B9`,
set only owned permission bit 6, then select `FE` last. Preserve unrelated
whole-byte override bits and reject drift. Bit 6 also bypasses a policy
contributor at `11156..1115E`; bit 5 suppresses reviewed sensor paths at
`F34E..F356`, `10428..10430` and `11BA2..11BAA`.

## Auto and observation

Normal selector `0..7` admission is at `14D48..14D56`. Supervisor `14732`
reads the selector at `14776`; `1477A` skips ordinary feedback `143D8` only
for `FE`. This normal feedback route does not require permission bit 6 to be
cleared first. Restoring a captured normal selector therefore leaves direct
duty and permits normal feedback before owned override/staging restoration.

Table reload depends on last-normal selector `8034F`, refresh/manual state,
and `8044E` at `15234` onward. A reload can clear manual flags at `154CE`
and updates last-normal at `154EE`; FE does not overwrite that cache.
Independently observe the restored selector with manual flags zero, then
restore owned `F0` and inactive `B8/B9` exactly. Never treat a successful
write, factory fallback, or cached HID response as complete handback.

Policy handler `9008` returns without dispatch when `80940` is zero at
`9010 -> 90A2`. Policy idle and host mailbox completion are separate;
neither proves all deferred firmware work has drained.

Actual duty counts are bounded against the live cycle: normal feedback loads
`F01807` and `F01841` at `1441A/1441E`. Initialization at `13682/13686` writes
`B7` (183), but does not justify hardcoding the runtime cycle. Counter-derived
speed reaches accumulator `80510` through `138FC..1392E`; `13A60/13A6C`
divide measured accumulators by 100 and publish `80816/80817`. A staged zero,
actual PWM zero, coarse tach zero and mechanically stopped fan remain distinct.

The source contract is complete for a separate SO695 1.01 two-channel
variant. Retain the shared guardian, fresh independent temperature sampling,
controller/lifecycle admission, bounded exposure, durable intent, ownership
checks, uncertainty fencing and caller-loss recovery. No firmware host-loss
recovery guarantee or physical result is established by this review.