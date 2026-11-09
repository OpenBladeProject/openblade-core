# Blade 17/18 mailbox trigger and status-writer investigation

Offline follow-up to [the conditional readback analysis](fan-stop-blade18-readback-resolution.md), investigated against repository HEAD `10caac37c5868ee8bbc4a54a392141facc5b5a34`. This report does **not** resolve runtime admission. It adds a weaker-trigger scheduling model, a newly identified lifecycle clear, an expanded IRQ/store inventory, and a correction to the chip-identification premise. No hardware, driver, updater, or vendor executable was run.

The important distinction is that arbitrary extra notifications do not alone produce a false successful read in the model. They can leave a stale empty callback that erases the next Automatic publication. The fresh-status predicate therefore has conditional data-validity evidence, but it is not a proven quiescence or restoration guarantee. An unrelated success-status writer would invalidate even data validity; the expanded firmware inventory has not excluded every computed writer.

## Exact inputs

All code addresses below are base-zero firmware file offsets/runtime instruction addresses. Each image is 262,144 bytes. Instructions are NDS32; embedded pointer-table words used here are little-endian. Scripts verify these hashes before inspecting the retained disassembly.

| Model / EC | Retained image | SHA-256 |
|---|---|---|
| RZ09-0423 / DI780EC1.05 | `artifacts/model-expansion/screen0423/ec.bin` | `ada7830bcca91f0201d108386a4c4efde39197bd31719f810db4481fd0e58dea` |
| RZ09-0484 / KI890EC1.07 | `artifacts/model-expansion/intel0484/KI890_EC_01_07.bin` | `a1d2e6ebe7de811661d0d61fd9e4ae108f8103fd8d65b1af72996faa1beadde2` |
| RZ09-0509 / KI8100EC1.02 | `artifacts/model-expansion/intel0509/block-07.bin` | `1d53c56d56581331c480bd12cb240b5657acc6ad5877711ffea2e4b8d7a073b1` |
| RZ09-0529 / KI8110EC1.06 | `artifacts/model-expansion/intel0529/block-06.bin` | `1cee6e614bfb086946f691cbc551863d6f697495159d23ede505df49fa47752d` |

Reproduction scripts and JSON/text results are under [`artifacts/model-expansion/it8320-trigger`](../artifacts/model-expansion/it8320-trigger/). Raw vendor images remain ignored artifacts. This work does not change the capability ledger, production code, tests, or physical-validation status.

## What arbitrary notifications do and do not break

[`check_arbitrary_notifications.py`](../artifacts/model-expansion/it8320-trigger/check_arbitrary_notifications.py) explores every transition in a finite abstraction of a single nonreentrant callback, a coalescing pending-event flag, and one host. At every point an arbitrary notification may set that flag, including while staging descriptors, resetting status, or performing EC-local clears. Dispatch consumes the flag before loading the opcode. Command loading, success status, payload copy, inner clear, outer clear, host command poll, status read, and result consumption are separate transitions. The model includes a subsequent Automatic publication to test restoration availability.

The host performs one fixed D0 read of `F01808`, count one; saves its result provisionally; resets `F02294` to zero; stages one fixed mapped A0 bank-zero/B2/count-one fence; and accepts only after observing `F02293=0` and fresh `F02294=80`. Every modeled publication, including subsequent A1 Automatic, resets status. There are no retries, delay-based claims, or writes after a modeled rejection. Descriptor staging is abstracted as intact and fixed; this model is not an I/O transport, field-alias, or firmware emulator.

The normal run explores **386 states**, including **14 accepted-fence states**, with **zero false-success states**. It also produces a witness that acceptance does not imply global quiescence and a concrete lost-Automatic witness. See [`arbitrary-notifications.json`](../artifacts/model-expansion/it8320-trigger/arbitrary-notifications.json).

The safety argument within these premises is:

1. After D0's inner clear, its data and status stores are already complete. Its remaining outer wrapper can erase the staged fence, but cannot restore success status after the host's fresh reset.
2. A zero-opcode callback returns from the command handler without setting status. Its outer clear can erase a newly published command, producing command zero with non-success status; it cannot manufacture success.
3. A nonreentrant A0 handler sets success before copying, but its only command clear is after copying. An earlier callback cannot run its outer clear concurrently with that A0 handler. Thus fresh status plus command zero cannot admit that A0 before its copy completes.
4. Additional notifications can arrange more zero-opcode callbacks. They do not add a status writer. Under these premises they can cause rejection and publication loss, but not a successful result without the corresponding copy.

The model's successful data-validity result is conditional on no unrelated status writer, no other host, no lifecycle reset or mailbox field corruption, intact ordered host operations, and the reviewed single-handler structure. It does not prove those firmware/transport premises.

The restoration counterexample is short:

```text
A0 copies its result; wrapper clears command; callback returns.
A pending event dispatches; handler latches opcode 00 and returns.
Host observes command 00/status 80 and accepts the compound read.
Host resets status, stages Automatic, publishes A1.
The old empty callback's outer wrapper clears command.
Host observes command 00/status 00 and rejects. Automatic was not applied.
```

The sequence preserves stop-on-ambiguity and produces no false confirmation, but an unexecuted Automatic handback remains material to supervised fan Stop. No finite number of repeated zero reads or elapsed delay distinguishes this suspended empty wrapper under arbitrary scheduling. A hardware-backed trigger/acknowledgement contract or another independent completion route is still needed to establish a stronger guarantee.

As a negative control, `--inject-status-writer` permits an unrelated asynchronous store of `80` to status. The resulting **586 states include 28 false-success states**. One witness has an empty callback latch zero, the host reset status, the unrelated writer set `80`, the host publish D0, and the empty wrapper erase D0. Command zero/status `80` then falsely accepts without a D0 copy. This is a counterexample to dropping the status-writer premise, **not evidence that such a writer exists**. See [`status-writer-negative-control.json`](../artifacts/model-expansion/it8320-trigger/status-writer-negative-control.json).

## Exact event path and the unresolved hardware meaning

The existing byte-backed handler proof remains relevant. Handler `203C` loads command at `2042`; `2046` branches on zero directly to a return at `2352` (0423), `2354` (0484/0509), or `23B8` (0529). That zero path has no status store. D0's inner clear is `2094` on 0423 and `2084` on the other three. The outer clear is separate:

| Model | Setup entry | IRQ pending setter | Event-13 callback | Outer wrapper / clear |
|---|---:|---:|---:|---:|
| 0423 | `115B0` | `B360` | `C11C` | `115DC` / `115E8` |
| 0484 | `122D4` | `BA18` | `C668` | `12300` / `1230C` |
| 0509 | `12868` | `BEE8` | `CB9E` | `12894` / `128A0` |
| 0529 | `12BB8` | `C1A0` | `CFC8` | `12BE4` / `12BF0` |

Setup stores `93` at `F02019` and `31` at `F0201B`, enables bit seven at `F01125`, and clears command. The IRQ setter writes RAM `8000D=1`, reads/ORs `80`/writes `F01124`, and reads/ORs `10`/writes `F0201B`. These operations and their order are established; calling them a particular interrupt match mode or write-one-to-clear contract is not yet justified by applicable register documentation. They are ACK-shaped operations, not a substitute for that documentation.

For 0529, the external IRQ demultiplexer at `3218..3224` indexes the table at `6000`. IRQ 71's word at `611C` selects `1C412`, which calls `C1A0`. The main dispatcher clears the software event flag before calling the selected callback (`1CB1E`, `1CB26`). Firmware order alone does not determine whether host descriptor/status writes or EC-local mailbox writes also trigger hardware notifications, or whether a latched hardware notification can arrive after software consumed its current flag.

The pinned [Chromium IT83xx header](https://chromium.googlesource.com/chromiumos/platform/ec/+/82d2822/chip/it83xx/registers.h), retained as `readback-resolution/chromium-it83xx-registers.h` with SHA-256 `e8251895319613e672f12d010aa5d8dea287d0ceee53f6d7a438348708029ec6`, supplies the GCTRL/BRAM base and IRQ-family context. It does not define the needed `+19`/`+1B` fields. Names appearing in the official [IT82302 PDF](https://www.ite.com.tw/upload/2024_01_23/6_2024012316175049je5fzt61.pdf) and [IT81202 PDF](https://www.ite.com.tw/upload/2024_01_23/6_20240123162204gq4mc18f22.pdf) do not establish applicability to these images; their trigger semantics are not imported here.

## Expanded computed-store and asynchronous-path evidence

[`scan_computed_stores.py`](../artifacts/model-expansion/it8320-trigger/scan_computed_stores.py) adds per-function control-flow constant propagation and address-overlap checks to the earlier literal/`+294` inventory. It resolves several forms of base construction, GP-relative stores, and constant ROM loads, discards disagreements at joins, and invalidates volatile state at calls. Unknown scalar-store addresses remain explicit. This is a candidate generator, not a sound whole-program alias proof: unsupported instruction semantics, omitted multiword-store analysis, imperfect function boundaries, and interprocedural arguments limit completeness.

| Model | Visited scalar stores | Constant-address stores | Unknown-address stores |
|---|---:|---:|---:|
| 0423 | 5,751 | 4,797 | 954 |
| 0484 | 5,666 | 4,600 | 1,066 |
| 0509 | 5,974 | 4,864 | 1,110 |
| 0529 | 6,104 | 5,009 | 1,095 |

A concrete new hit is a command clear through **`F01000 + 1293`**, missed by looking only for a base `F02000` and offset `293`. Its instruction bytes are `10 10 12 93` in all four images:

| Model | Lifecycle function | Computed command-clear instruction |
|---|---:|---:|
| 0423 | `B1E8` | `B21E` |
| 0484 | `B8A0` | `B8D6` |
| 0509 | `BD70` | `BDA6` |
| 0529 | `C028` | `C05E` |

The retained listing has one direct call to each function, at `303E`, on the startup/resume branch selected by prior RAM `80145 == 33`. The function disables the relevant interrupt-enable bit, clears command, and performs the interrupt-status operation. This is a lifecycle source of command zero, not a newly identified asynchronous success-status writer. The active-session/no-reset premise cannot silently include this path. The byte-backed windows and the startup branch are in each `*-new-proof.txt`.

[`irq_reachability.py`](../artifacts/model-expansion/it8320-trigger/irq_reachability.py) starts from all 240 pointer entries at ROM `6000` (163 distinct targets), follows retained direct instruction edges across function boundaries, and expands bounded indirect tables found along those paths. Every vector target has retained disassembly. Reset entry `5000` is an explicit stopping boundary. After resolving the observed six-entry/21-entry switches and eight-slot callback table, this traversal has no remaining indirect edge **within that traced graph**:

| Model | Reachable instructions | Unknown scalar stores in IRQ closure | Eight-slot callback table / enabled callback |
|---|---:|---:|---:|
| 0423 | 3,027 | 75 | `2A600` / `1B346` |
| 0484 | 3,385 | 106 | `2AA64` / `1B986` |
| 0509 | 5,218 | 83 | `298E0` / `18816` |
| 0529 | 5,224 | 83 | `2A070` / `18F4A` |

Resolved indirect calls retain both the callee edge and return fallthrough. A regression assertion checks that each model's resolved callback call visits both successors; the JSON records these checks. These counts include the corrected callee traversal.

Only slot one of each callback table is enabled (selector `03`); the other seven have selector `FF` and are skipped by the inspected loop. Its loop-result stores are at RAM `871A0..871A7`, not mailbox status. The emitted JSON retains each resolved target and direct path to a known target store. No constant-resolved `F02294` store appears in this IRQ closure. The relevant known stores are the software pending flag and trigger-control operation.

That result does **not** exclude computed status writes. Some unresolved stores belong to generic buffer helpers reached through interrupt code. A concrete 0529 call chain is `660C -> 697E -> 7E70 -> 7B2C -> 7974 -> 791E -> 21198 -> 21060 -> 20EE2`; pointer destinations in that chain have not been proved disjoint from the mailbox. Other roots/exceptions and multiword writes are not exhausted by this vector-table traversal. An exact little-endian pointer-word scan found no literal `F02294` address, but dynamically constructed addresses need no literal. These are specific remaining obligations, not grounds to declare writer uniqueness.

## Chip identity: family evidence is not an exact silicon binding

Each image carries `ITE8320EC` at file offset `90`. That identifies the firmware/container family; it does not by itself pin the installed silicon model or revision. The relevant primary definition could come from an applicable IT83xx/IT8xxx BSP if its binding is independently established. Requiring an exact IT8320 datasheet solely from this header would overstate the evidence.

The shared retained vendor flasher (`intel0509/block-12.bin`, also identified in the 0423/0484 extraction) has SHA-256 `3de34bf3f5e57e0c11e76f45b773642ae3816f4a645040cf3b0602dcd30e79ed`. Its only direct `RWIo.Read_sio` caller in the retained IL is `Program.show_spi_id`, method token `06000030`, RVA `24AC`. After the `FF/FF/FE` SPI identification branch, IL `011A/011C` reads SIO index `22` and masks its upper nibble to choose flash capacity. It does not read SIO indices `20/21` and compare an EC model. `Read_sio` itself is token `06000061`, RVA `3B7C`; `Enable_EC_Flash`, token `06000062`, RVA `3BA0`, configures logical devices rather than admitting an exact chip ID. See the independently hash-checked [`vendor-flasher-identity-il.txt`](../artifacts/model-expansion/it8320-trigger/vendor-flasher-identity-il.txt). This is a claim about these retained managed methods, not every native component in the carrier.

There is stronger firmware-family evidence than the header. All four images have the same routine comparing `F02000 == 83`, then checking `(F02002 & 0F) < 3` before a guarded register sequence:

| Model | Comparison routine | First ID-byte read | Revision-byte read |
|---|---:|---:|---:|
| 0423 | `23B88` | `23B8C` | `23B92` |
| 0484 | `24200` | `24204` | `2420A` |
| 0509 | `22D48` | `22D4C` | `22D52` |
| 0529 | `23488` | `2348C` | `23492` |

The primary Chromium header identifies these offsets as the two-byte-ID family's `CHIPID1` and `CHIPVER`. Chromium's pinned [2024 identification-code change](https://chromium.googlesource.com/chromiumos/platform/ec/+/68af2030e8a45d14a7f60db7dfb20bf37ab9d24a%5E%21/) also shows why an `83` first byte is family evidence rather than a complete model/revision identification: the removed test covered older chips while different ITE families needed different flash-size tables. The Razer routine has both taken and untaken paths and does not compare the second ID byte. Its presence therefore does not prove which silicon/revision is installed, or that the guarded branch runs. The instruction bytes are retained in `*-new-proof.txt`.

## Remaining decision and concrete next evidence

The compound reader remains **conditional, not resolved**. The broader scheduling model corrects an overly broad failure claim: duplicate/extra notifications alone do not defeat fresh-status data validity under the stated writer and execution premises. They still prevent claiming that accepted completion leaves no stale callback capable of erasing a later restoration publication.

The missing hardware fact is the applicable definition of `F02019`/`F0201B`: which master, access type and address match create a notification; what writing `31` selects; what ORing `10` acknowledges; and whether that acknowledgement prevents later delivery from descriptor/status/EC-local accesses. A source binding to the actual chip family/revision is needed, not an unrelated part's identically named register. The remaining software proof obligation is that every asynchronous/computed store reachable during the active session is disjoint from success status, or cannot synthesize the accepted status/payload pairing. The unresolved generic-buffer argument chain above is a concrete offline lead for that work.

No currently retained primary source closes either obligation. This investigation stops at those precise gaps rather than converting the model into runtime authority. The separately investigated secondary mailbox route is outside this report's scope.

To reproduce from the repository root with the existing Python runtime:

```powershell
python artifacts/model-expansion/it8320-trigger/scan_computed_stores.py
python artifacts/model-expansion/it8320-trigger/irq_reachability.py
python artifacts/model-expansion/it8320-trigger/extract_new_proof.py
python artifacts/model-expansion/it8320-trigger/extract_host_identity.py
python artifacts/model-expansion/it8320-trigger/check_arbitrary_notifications.py
python artifacts/model-expansion/it8320-trigger/check_arbitrary_notifications.py --inject-status-writer
```

These scripts read local data and write only analysis artifacts. They neither execute the retained vendor software nor access device ports.
