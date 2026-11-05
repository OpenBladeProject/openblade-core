# SO6120 host mailbox and pause-command alternatives

The 2026-11-05 offline follow-up establishes a second vendor host path to the
EC register map: the supplied EC updater accesses a mailbox through host I/O
ports `0x300/0x301`. This path avoids the MCU's shared transfer descriptor.
It does not establish exclusive control of the EC fields, a supported OpenBlade
transport, or recovery after the host stops responding.

The strongest MCU pause candidate also falls short: class `00/04`, value `5`,
opens generic access without excluding the normal bus workers. These results
refine the [ownership audit](so6120-mcu-transfer-ownership.md). They do not
enable a Silent fan-stop test or extend support to other Blade models.

## Exact inputs and method

The analysis uses the same SO6120 EC 1.04 and MCU 1.07 images pinned by the
[offline profiles](../tools/firmware/profiles/). Rechecked SHA-256 values:

| Input | SHA-256 |
|---|---|
| Supplied EC updater, 652,560 bytes | `e6e105ca059b96c7dd0ae0bc2fbc0e259b29250b97f09447044624f2447cdae7` |
| Extracted updater block 3, 390,856 bytes | `e3b25313595d1fd2a7c47fa46d6cbda45ae596e783eaa3fd771dafcf495d3358` |
| EC image, 262,144 bytes | `8537e9fd55b7b35504d8d87b0191acc4391b67365b3fe72f6e1029a424c9e707` |
| Decoded MCU image, 57,764 bytes | `90bbe0d8dd1ca31614a2465c7c8821347b2a667606c39bedadbd59b98886344f` |

The updater was read as PE/.NET metadata and IL with `PEReader`; its assembly
was not loaded or executed. Regenerated IL matched the previous dump, with
native import names added. Firmware conclusions use recovered instructions
and exact bytes; pseudocode and vendor method names are supporting evidence.
No hardware access, installation, service change or flashing occurred.
Full dumps and verification scripts remain in ignored local research artifacts.

## Vendor host mailbox path

`RWIo.WriteRzEcMbox` (method token `0600001F`, RVA `0x2167`) writes an index
to host port `0x300`, then its byte to `0x301`. `ReadRzEcMbox` (`06000020`,
RVA `0x217F`) also writes the index, then reads `0x301`. Thus even the
vendor's mailbox read requires host I/O writes. `IoManager` selects imported
`Inp32/Out32` from `inpout32.dll` or `inpoutx64.dll`; this is separate from
OpenBlade's admitted HID transport. The updater constructor calls `IoINIT`,
and `MainWindow.Qevent` contains calls to the mailbox wrapper. This establishes
vendor code use, not a physical test of this route on the current machine.

`RWIoI2C.I2C_Read_Write` (`06000051`, RVA `0x2E90`) implements the same
layout as the exact EC handler `0x203C`:

| Host mailbox index | EC MMIO | Meaning |
|---|---|---|
| `90` | `0xF02290` | Bank |
| `91` | `0xF02291` | Register index |
| `92` | `0xF02292` | Byte count |
| `93` | `0xF02293` | Command: mapped read `A0`, mapped write `A1` |
| `94` | `0xF02294` | Result; `80` is the wrapper's success value |
| `A0..BF` | `0xF022A0..BF` | Up to 32 data bytes |

For the accepted device/address argument `0x58`, the <=32-byte read branch
sets bank/index/count/command at IL `0014..0043`, polls command zero at
`0048..0052`, then reads data. The write branch populates fields and data at
`017E..01CE`, then polls at `01D3..01DD`. Both finish by checking status at
`02D2..02E6`. Neither inspected branch checks for an idle mailbox before
replacing its fields, acquires a generation/token, or bounds that polling loop.
The wrapper's 33..64-byte branches split the request into two transactions;
they do not make the operation atomic.

The mailbox bank is separate from the SMBus bank selector at EC `0x8144C`.
At `0x22D6..0x22EC`, firmware takes the bank directly from `0xF02290` and
selects the common mapping table. A mailbox request can therefore select a
bank without first changing the SMBus selector. However, a mapped write to
index `0F` still changes that shared selector; the vendor wrapper additionally
updates its own `EC_BANK` byte for that index. This distinction does not isolate
the underlying mode, permission or duty fields from other writers.

The EC handles this mailbox through its event dispatcher. Function `0xBEE6`
sets event byte `0x8000D`; slot 13 in the table at `0x2E654` contains
`0xCD64` at `0x2E688`. Dispatcher `0x2115E` clears a pending event and calls
its slot; it is called at `0x3084`. The selected wrapper calls `0x12D80`,
which invokes `0x203C` and clears command `0xF02293` at `0x12D8C`.
This supplies an EC event path, not an MCU bus reservation or an exclusive
host mailbox owner.

Completion remains weaker than applied-state confirmation. Firmware validates
bank <=7 and count <=32, then sets status `80` at `0x22F4`, before processing
the mapped bytes. The write loop skips read-only entries at `0x2388` while
leaving that status intact. Writable entries receive whole-byte stores at
`0x2390`. Length/index fields are reread during the loop; the examined path
does not establish an immutable request snapshot. Command zero plus status
`80` is therefore insufficient to prove a particular fan setting changed.

## MCU state 5 is an access gate, not a pause

The recovered class-00 switch table at `0x71CE/0x71CF` maps command `04` to
`0x729E`; the paired table at `0x720C/0x720D` maps `84` to the state getter.
For requested value `5`, instructions `0x72C4..0x7302` store application
state `0x100001E8` and set generic-access gate `0x100000C1` to one. This
branch leaves thermal cache validity, pending work, queue delay and transfer
state unchanged. The getter returns the state byte, not a drain acknowledgment.
No external mode name is inferred from the number alone.

The worker's queued-work entry `0x4B5E..0x4B82` checks cache validity,
pending work and delay without testing this state byte. In the periodic
peripheral writer, one path tests state at `0xA96C` after its transfer at
`0xA95C`; the alternative transfer at `0xA98A` bypasses that test. Thus
selecting value `5` does not exclude already eligible transport clients.
This is a static concurrency concern, not an observed collision.

Value `1` reaches the nonreturning reset path `0x46B4`; it is not a reversible
pause. Generic `00/21` and `00/A1` still call the shared helpers from the
command handler. `00/58` ORs pending bit 8 instead of draining the queue.
The reviewed class-07 and normal thermal handlers enqueue fixed settings,
without an arbitrary-register work record or per-operation completion token.
The separate larger report branch inspected handles frame-buffer updates.
Coverage is bounded to these recovered candidates and their dependencies;
other interfaces and computed paths have not been exhaustively reconstructed.

## The named lifetime routine is not a cooling lease

The updater contains `RWIo.Enable_Finite_Life_time` (`0600004E`, RVA
`0x2D87`), but its name does not describe a fan timeout. It writes command
`22` at index `12` through a different legacy mailbox, using ports
`0x302/0x303` assigned by the class initializer. In the corresponding EC
dispatcher `0x12E2C`, the command-22 branch at `0x132BC..0x132CC` requires
`0x80450 == 87` and calls `0x16992`. That routine copies five bytes from
`0x80200..04` to `0x87F10..14`, then calls persistent-storage routine
`0x1679A`. This is not an established volatile fan-recovery contract; the
updater method must not be repurposed as a safety timer.

A separate legacy-mailbox countdown at `0x8044E` is also insufficient.
Expiry routine `0x1847C` calls `0x1819A` only once command `0xF02212` is
zero; otherwise it holds the count at one. The cleanup clears legacy mailbox
fields and its flags/count, without clearing direct-duty permission or
restoring the fan selector. The direct-duty branch is evaluated before the
ordinary fan path's countdown check. Neither the method name nor that
countdown supplies bounded cooling recovery from a failed host.

## Legacy timed commands are storage transactions

The parallel follow-up traces commands `F0/F1/F3/F4/F5` instead of inferring
their purpose from the shared countdown. `F0` starts a checked storage read and
`F1` continues it. `F3` prepares write-session state; an admitted `F4` already
reaches persistent directory, metadata and payload writes. `F5` continues the
payload transaction when its session and continuation gates pass.

The concrete Silent example is record `0x2D`: the `F4` dispatcher branch at
`0x17CBE..0x17CC2` reaches `0x17F14..0x17F20`, then `0x180D6/0x17B50`.
The latter calls `0x17A90` for directory/metadata and `0x17934` for payload
work; `0x178D2` erases and programs payload sectors. Ordinary loader
`0x17026` selects `mode + 0x28`, so Silent selects this record. This policy
record loads 256 bytes into `0x87C00`, distinct from the 512-byte fan-curve
record `0x25` loaded into `0x8A000` by `0x158FE`. `F4` separately admits that
curve record at `0x17C12..0x17C16`, reaching `0x17E96..0x17EA2` and the
same persistent write chain. Neither stored record is an expiring override.

While countdown `0x8044E` is nonzero, `0x16318..0x1632C` skips the conditional
policy/curve reload and initialization block. The continuation still reaches
manual clamping and automatic stage logic. Periodic supervisor `0x1586A`
also continues its ordinary tach/PWM path without a countdown check. The
storage-session commands do not supply temporary target or duty values.

Countdown handler `0x1847C` is reached through `0xCBEC/0xCC32` and pending-work
dispatch `0x21038..0x21048`. The refresh value `0x78` is an invocation count,
not a verified time bound. Expiry and explicit `F2` both use cleanup `0x1819A`;
it clears mailbox state without restoring the old record, target or PWM, or
requesting a policy refresh. Even at zero count, unchanged mode and clear
refresh/transition flags can bypass reload at `0x15E84..0x15EC8`.

This rules out the reviewed legacy family as a volatile fan-stop lease. Its
persistent write path is not proposed as a test or implementation. The parallel
[Silent Automatic review](so6120-silent-automatic-policy.md) instead resolves
the existing seven-input zero-stage conditions and ordinary restart path.

## Consequence for a Silent experiment

| Candidate | What is established | What it does not establish |
|---|---|---|
| MCU generic HID access | Known forwarding path to the EC map | Exclusive use of the shared MCU bus state |
| MCU application state 5 | Enables the generic gate | Worker pause, drain or transaction ownership |
| Vendor host mailbox | Separate host route and per-request bank selection | Admitted OpenBlade transport, exclusive mailbox/field ownership or thermal recovery |

The direct mailbox removes one specific MCU-buffer concern; it does not resolve
the [direct-duty recovery and restoration gaps](so6120-direct-duty-safety.md#decision-and-remaining-evidence).
Copying the updater's InpOut path would also add a raw-port driver dependency
outside the current architecture. The next useful evidence must establish a
supported access contract and bounded recovery, rather than merely another
address that can reach the same bytes. Normal Silent Automatic's zero-target
stage remains the separate existing-policy route for an eventual idle comparison.
