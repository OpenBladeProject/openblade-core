# Blade 16 (2023) Stop/Auto source review

The standard RZ09-0483 (`1532:029F`) SO690 EC 2.01 implements the bounded
host-mailbox, direct-duty and Auto sequence used by the guarded Stop backend.
This is an independent firmware and updater review. Physical fan stopping and
guardian restoration have not been tested on this model.

The [Lamborghini SO695 firmware](fan-stop-so695-source-review.md) has a separate
review and admission entry. Standard SO690 admission requires the exact
product/HID, reported EC 2.01 and mapped board identifier `3006`; SO695 requires
EC 1.01 and identifier `3009`. These are compatibility checks, not live image hashes.

## Pinned inputs

The [official update](https://mysupport.razer.com/app/answers/detail/a_id/14883)
links the [SO690 EC carrier](https://dl.razerzone.com/drivers/Razer%20Blade%2016%22%20%282023%29%20%7C%20RZ09-0483/SO690_EC_0201.exe).
It was downloaded and extracted as data; no updater was executed.

| Input | Bytes | SHA-256 |
|---|---:|---|
| `SO690_EC_0201.exe` | 1,503,488 | `a8693bebfcfdd3999f5fce53bc41d77a382d44cb3c368fd928de613488cbab15` |
| NSIS block 11: `SO690_EC_02_01.bin` | 262,144 | `cbee52c89db0e9415865867ba053b415e0239bbec56a41ab25a35013370fca3c` |
| NSIS block 14: x86 InpOut library | 98,304 | `01bcec6ddb4964e1f5b69ba1bd3876221d8de7ae17cacfac66f095013434a78f` |
| NSIS block 15: x64 InpOut library | 98,304 | `5f27ed4d5cd58a1ee23deeb802e09e73f3a1d884ce2135f6e827f67b171269e7` |

The carrier's Authenticode signature verified as Razer USA Ltd. Block 16 binds
SKU `RZ09-0483`, board `1A583006`, EC `02.01` and `Razer_EC_Flash_v2.1.exe`.
Block 13 names the exact EC image and board `3006`; blocks 7/8 target
`1532:029F`. These establish applicability independently of the native library
hashes, which also match the previously reviewed SO6120 libraries.

The existing [adapter-power source review](synapse-adapter-wattage.md) separately
pins Product 671's active `07/8C` getter and wattage decoding. The guardian uses
that read-only query; it does not use the updater's generic command interface.

## Mailbox and completion

Block 10's managed `RWIo.WriteRzEcMbox` (RVA `36B5`) writes the index to port
`300`, then the value to `301`; `ReadRzEcMbox` (RVA `36D0`) selects at `300`
and reads `301`. Its `RWIoI2C.I2C_Read_Write` (RVA `3D80`) binds address `58`
to bank/index/count at `90/91/92`, payload at `A0`, and command `A0`/`A1`
published at `93`. It waits for `93 == 0`, then checks status `94`. Block 3
contains the same mapping. P/Invoke imports identify the InpOut libraries.

EC function `203C` independently confirms MMIO `F02290..94` and `F022A0`.
Bank is bounded to 0..7 and count to 32. Metadata 1 ignores a mapped write;
the reviewed writable descriptors store the byte and set change flag `8000E`.
Status `80` is set before
the loop, so it is insufficient as completion evidence. Caller `11D28` calls
the handler at `11D2A` and clears command `F02293` at `11D34` after return.
The shared transaction's command-clear, status, descriptor check and separate
fresh A0 readback therefore apply without changing the primitive.

Reset instructions `50A4..50CA` copy ROM `2C7A8` to RAM `81000` (length `15C`).
The bank-pointer array at ROM `2C8C8` becomes RAM `81120`, as used by the handler.
Bank zero begins at ROM `29784`; each descriptor is four-byte little-endian
metadata followed by a four-byte address.

| Logical index | Access | Backing location | Meaning |
|---|---|---|---|
| `06` | Read | `802E3` | Active bit 0 |
| `08/09` | Read | `80816/80817` | Measured tach, 100 RPM/count |
| `0A/0B` | Write/read | `80940/80941` | Separate policy request/status |
| `1A` | Write | `8035F` | Thermal selector |
| `36` | Write | `8034D` | Manual flags |
| `B0/B1` | Read | `F01807/F01803` | Actual PWM duty |
| `B2` | Read | `F01841` | PWM cycle |
| `B8/B9` | Write | `80394/80395` | Staged direct duty |
| `F0` | Write | `80425` | Override flags |

These are all 13 logical registers used by the shared core. Runtime uses the
logical indices, not these internal RAM addresses.

The additional read-only bank-zero identity fields E3/E4 map to `803A2/803A3`.
Initialization `AD14`, `AD32` and `AD36` publishes `06/30`, the little-endian
family identifier `3006`. The updater's own mapped identity check and the
separate SO695 values are detailed in the linked variant review. The guardian
verifies both bytes through journaled mapped reads before setting changes.

## Stop and Auto

Policy `14D1C` requires active bit 0 at `14D20..14D2A`. Selector FE branches
at `14D44..14D48` to `15168`, where F0 bit 6 is checked. With permission set,
`15196..151B0` copies B8 directly to fan 1 PWM and `151C6..151E0` copies B9
to fan 2 PWM. Zero receives no special handling or minimum clamp. Requested
RPM bookkeeping is separate from the measured tach fields; the latter are
updated by the counter path and divided by 100 at `13A50` onward.

Without permission, FE falls back to the saved factory selector at `15172`.
That is not an exact restoration. The guardian restores the captured normal
selector first, observes Manual zero, then restores owned F0 and staging bytes.
Normal selectors 0..7 follow `14D4C..14DA0` and the ordinary feedback policy;
FE does not overwrite the previous normal-selector cache `8034F`. A normal
selector also resumes the feedback supervisor: `1477A..1477E` skips feedback
`143DC` only for FE, without requiring F0 bit 6 to be cleared first. A normal
table reload is conditional on prior selector, refresh/manual state and `8044E`.
It can clear manual flags; these must still be independently observed, never
repaired by an unplanned write. Policy handler `9008` returns at `9010` when
request `0A` is zero. Idle request is necessary admission, not a drain token.

F0 is a whole-byte register. Bit 6 also bypasses a policy contributor in `11114`;
other bits have unrelated effects. Preserve them and reject unexpected drift.
Duty counts must be checked against the freshly read nonzero cycle. Duty zero,
tach zero and mechanical stopping remain separate observations.

There is no firmware host-loss lease. Existing guardian ownership, process
handoff, independent temperature sampling, lifecycle handling, durable journal,
fresh admission and uncertainty fencing remain required. A matching descriptor
does not establish exclusive access against firmware or another port client.
Software failure/recovery tests cover the shared sequence; they are not this
model's physical validation.

The [offline profile](../tools/firmware/profiles/so690-ec-2.01.json) exposes this
exact image's bank map and reviewed annotations. It supplies no live access.
