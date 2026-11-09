# Fan Stop reader and EC follow-up

Reviewed 2026-11-08 against `2aa6c4a5`. This follow-up covers the four
three-channel Blade 17/18 ECs and seven models that still need receiver-side
EC evidence. The retained vendor inputs were parsed as data. The results
below are from offline analysis and models of firmware ordering.

## Blade 17/18 read completion

The four pinned images expose a legacy read that uses different scratch
fields from [the legacy raw-address reader](fan-stop-bram3-readback-review.md).
Command `11` reads a byte from `80200 + index30`, writes it to result `32`,
then clears legacy command `12`. This arm leaves `13..15` alone. A zero
opcode branches directly to return and skips the clear. Index `E3` reaches
the same active-state byte as primary bank 0, index `06`.

| Model | Command 11 arm | Result store / command clear | Calls from scheduled event 6 to dispatcher |
| --- | --- | --- | --- |
| RZ09-0423 | `11A1A` | `11A2C` / `11DDA` | `1D02E → 1D01E → BA74` |
| RZ09-0484 | `12740` | `12752` / `12AFE` | `1D66E → 1D65E → C130` |
| RZ09-0509 | `12CDE` | `12CF0` / `130A6` | `1C1B6 → 1C1A6 → C62C` |
| RZ09-0529 | `1305C` | `1306E` / `13440` | `1C8F4 → 1C8E4 → C91C` |

Event 6 and the primary mailbox event 13 are dispatched from the same
firmware loop. If those callbacks execute serially, a fresh command `11`
reply to a request published after the D0 copy proves the D0 wrapper has
returned. The host does not have to publish a primary A0 fence while D0
still has its second clear pending. The candidate is one compound
observation with its complete sequence chosen before the first publication.

The initial zero-only model clears primary status, seeds its payload with
`FF`, and keeps the third-duty zero provisional after D0. It clears legacy
result `32` to zero before the fixed `11/E3` publication. Legacy command zero
and a reply with active bit 0 set complete the observation.

| Model conditions | Explored states | Acceptances | Result |
| --- | ---: | ---: | --- |
| Serialized callbacks, intact staging, exclusive host | 171 | 8 | Each accepted read has both copies and D0 has returned. |
| Same, with an independent success-status writer | 242 | 8 | The payload sentinel still excludes acceptance without the D0 copy. |
| Independent status writer, stale zero payload allowed | 368 | 16 | False acceptance without D0 copy. |
| Cross-callback reentry allowed | 288 | 20 | Acceptance can precede the D0 return. |

The model permits extra primary notifications. In the first two rows a later
empty event 13 can still erase a subsequent Auto publication. A legacy
completion proves the end of the specific D0 wrapper under serialization.
The primary notification source still needs an applicable trigger contract
or an independent, preselected Auto route. An erased or ambiguous command
ends the attempt. The candidate never switches transports after a failed
publication.

The constant-address inventory found legacy reply and command stores in the
dispatcher. It also rejected apparent offset `230/232` matches that resolve
to `87230/87232` scratch and `80232` RAM. All 8,192 mapped descriptors were
checked for exact aliases to `F02212/30/32`. Startup clears and computed
stores remain outside that limited inventory. The staged fields and sentinels
need verified ownership across both windows, including exclusion of other
commands that can write the primary payload. The reader's time bound and
actual port behavior remain unmeasured. The `90` reader still has its
previously identified descriptor-sharing race.

### Duty values beyond zero

All four images program cycle register `F01841` to `B7` (183). The
identical instruction sequences start at `137E6`, `13CDA`, `24BCE` and
`2530E` in model order 0423/0484/0509/0529. Their bank-zero `B2` descriptors
read that register. A candidate full-range observation can reserve `FF`
in the reply and require a fresh cycle in `01..FE` and duty at or below
that cycle. This also covers a nonzero baseline or Auto observation.

The exhaustive byte-domain check passed 131,072 combinations of cycle,
result and whether the copy occurred. Under intact staging and payload
ownership, a lost copy leaves `FF` outside the accepted range even if an
unrelated writer sets success status. Cycle zero and cycle `FF` fail this
predicate. The check separates the sentinel from legitimate duty values.
The compound observation still requires the callback ordering and
completion conditions above.

## Independent EC I²C route

The asynchronous store review traced the IRQ formatting branch to three
fixed ROM formats. The longest writes at most 92 bytes including its NUL
into a 260-byte stack buffer. Decoding GP-relative instructions also resolved
nine previously unknown store addresses per image to ordinary RAM. These
results close those particular alias leads. The dynamic destination in the
I²C receive handler requires its own bound.

Each exact image initializes an eight-bank descriptor pointer table from
ROM. The I²C receive and transmit handlers use that table. In bank 0,
`1A` addresses selector `8035F` and `F0` addresses override `80425`, both
with metadata `2`. These accesses do not use primary command `93`. The
valid descriptors have no alias to third actual PWM `F01808`, primary
`93/94/A0`, or legacy `12/30/32`.

| Model | RAM descriptor pointer table | I²C RX data store / TX data load | Address `58` at setup call / store to `F01C48` |
| --- | --- | --- | --- |
| RZ09-0423 | `81120` | `2512` / `2494` | `15DC2` / `2457A` |
| RZ09-0484 | `81120` | `2512` / `2494` | `162E2` / `24BF2` |
| RZ09-0509 | `8113C` | `2AEA` / `2A6C` | `271D8` / `2373A` |
| RZ09-0529 | `81140` | `2B4E` / `2AD0` | `279C0` / `23E7A` |

Register `0F` maps to the I²C bank selector in every valid bank. The
receive path can write that byte without a local `<8` check. The alias
conclusion therefore depends on a valid bank value and intact pointer table.
Exact master-side request construction and exclusive ownership are material
to the proof. Initialization sets bank zero and address `58`, matching
write address byte `B0` on the keyboard MCU bus.

The exact keyboard images establish the other side of the endpoint. The
first three are in their respective [pinned EC carriers](fan-stop-blade18-source-findings.md#exact-inputs).
RZ09-0529 uses its separately signed keyboard updater, which targets
SSID `1A58300F` and PID `02C7`. Its carrier SHA-256 is
`30c06ba3d2d057593bbf3c812249d4edd020d17fc157a8cc7c29264a9e72f9dc`.
[Razer RZ09-0529 keyboard updater](https://mysupport.razer.com/app/answers/detail/a_id/19865)

| Model | Decoded MCU version and SHA-256 | Queued selector / commit / mode calls | Gated immediate read / write calls |
| --- | --- | --- | --- |
| RZ09-0423 | 1.02.00 `abe4b5db22faa2d73d7da390fc6b00844ab48d4e957519dcf62d666dd22bb350` | `278E` / `27A4` / `2660` | `B3E6` / `B408` |
| RZ09-0484 | 1.04.00 `e19171df923e0c786f9a0ee19f3080b3fc121842d7697d7fdc68cdd02b83fb5e` | `278E` / `27A4` / `2660` | `B682` / `B6A4` |
| RZ09-0509 | 1.01.00 `32bb690b242b524be8432e838e587e1ffd495df413eafb93341d4479d750fc1f` | `278E` / `27A4` / `2660` | `B8F4` / `B916` |
| RZ09-0529 | 1.07.00 `c0ebb3b40d3ac763a50d7301457d73f1a84a3e2c951730454d848b13ca3f3578` | `4FE2` / `4FF8` / `4F00` | `7860` / `7872` |

On each keyboard MCU, HID `0D/02` queues selector and mode. The worker
uses `B0:1A`, then `B0:0A` with value `05`, then `B0:36`. The
`0D/82` reply comes from the shared cache. SET can update it before the
bus write. A matching reply therefore cannot confirm Auto application.
Bank 0 maps `0A` to `80940`. The shared jump table at `902C` dispatches
value 5 to `9032`, where the handler zeros reply `80941` and sets bit 0
of `8033F`. Event 6 tests that bit before invoking the thermal worker.
The bus acknowledgement and this scheduling flag precede independent
confirmation of the applied thermal state.

The class `00` read/write arms use the same bus helpers synchronously and
propagate bus status. `00/22` sets a Boolean gate and `00/A2` reads it.
Startup sets the gate to zero. The immediate read uses a combined
address/read transfer and writes the reply bytes into the HID response.
It does not write EC register data. The gate admits both read and write
arms. The caller-controlled write can reach the I²C bank selector.

The known autonomous constant-address writes to `B0` avoid register
`0F`. One four-byte transfer begins at `90`. In bank zero, those logical
indexes address RAM `803C0..803C3`. None of the valid mapped descriptors
in any bank points into primary mailbox `F02290..F022A0`. The long
table-addressed LED writes use slave bytes `40/5E/4A`, with `54` also
present on KI8110. The fixed and RAM-indexed callers bound the selected
entries to those values. Indirect paths and forwarding callers still
limit the ownership conclusion.

This establishes an independent EC byte transport for these exact MCU
images. Its gate and bank ownership need a bounded contract.
Verified Auto also needs independent confirmation of the captured normal
selector, stage bytes and override permission. A guardian using this
transport must retain its handback ability if the caller exits.

### Observe the bank and verify the applied restore independently

Primary A0 with bank zero, logical `0F` and count one reads the I²C bank
byte through the primary table. It selects that table from `F02290`,
independent of the selected I²C bank. This makes a non-mutating bank
observation possible while keeping the exact primary completion and
payload checks. A confirmed zero is a point-in-time observation.

Across all four images, bank zero maps the six possible restore fields
`1A/36/B8/B9/9C/F0` to the selector, manual state, three stage bytes and
override, all with metadata 2. Banks 1 through 7 give those six indexes
metadata 1. The I²C RX path diverts writes for metadata 1 to scratch. For
bank 1 the six read descriptors all alias a single reset-cleared RAM byte:
`81449` on 0423 and 0484, `8144D` on 0509, and `81451` on 0529.

The model starts in FE with override bit 6 set and a captured all-zero
normal baseline. If another master selects bank 1 after the initial
bank-zero check, the I²C writes can leave FE and the override intact while
the same-bank reads return zero for every field. Returning the bank to
zero before the final bank check makes both checks pass. This witness
appears in the 64-state model for each image. Verifying each affected
field through fresh primary bank-zero A0 removes that false acceptance
in the 33-state model. The model permits valid bank drift alone and
assumes intact primary reads. Out-of-range bank selection remains an
addressing hazard before any readback can detect it.

The RX handler also diverts a single write starting at logical `36` in
bank zero if current manual-state bit 7 is set. The 0529 branch is
`2B1E..2B42`. The operation must preserve a matching field and verify
the actual byte if restoration needs to change it. The map alone cannot
establish that the write applied.

The only constant-address bank store found in the retained instruction
inventory is the setup write of zero. The 0529 watcher at `279D4` can
re-enter setup, which may interrupt a transfer. A later zero observation
does not settle an earlier ambiguous write. The I²C bank selector and
primary writes can still change the same byte. The route needs exclusive
ownership, bounded
transport results and a guardian-held handback path. The present guardian
has its port owner through caller exit. This source review has yet to
establish an equivalent independent HID lifetime for the I²C route.

### Interrupt writers and command publication

The interrupt-rooted store review now accounts for the earlier unresolved
computed stores, including the KI890 SMBus path. A second pass over the
instruction graph found 94 scalar stores omitted by the function inventory.
Their resolved destinations avoid both descriptors, the interrupt
configuration, and the third fan's duty register. All four images also have
stack-relative multiword stores. These bounds rely on the retained interrupt
roots, valid stack and ABI, intact descriptor pointers, and I²C banks 0–7.
Each image still has a bank-dependent I²C receive store, so this result does
not prove mailbox ownership against another host master or a main-loop
callback.

The I²C handler adds a byte-sized start index to a saturating transfer
counter. Receive-side lookup can reach index `1FF` even with a valid bank.
The pinned images were checked at indexes `000..1FF` for all eight banks.
None of those pointers targets either mailbox descriptor, actual duty 3, or
the I²C pointer and counter slots. The extended descriptor at `10F` in
banks 1–7 does point to the writable bank-selector byte in all four images.
A bank-ownership argument must bound the whole transfer, including
its length and any pending command.

### Primary interrupt completion

| Model | Scalar stores in graph | Missing from earlier inventory | Lifecycle store to actual duty 3 |
| --- | ---: | ---: | --- |
| RZ09-0423 | 456 | 22 | `139DC` |
| RZ09-0484 | 506 | 22 | `13ED8` |
| RZ09-0509 | 799 | 25 | `24DEE` |
| RZ09-0529 | 803 | 25 | `2551C` |

The known stores near the primary descriptor touch `F02295..97` and the
interrupt acknowledgement at `F0201B`. A lifecycle path on each image
directly clears actual duty 3 at `F01808`. A zero duty read taken across
that transition is insufficient to confirm Stop. Session and power admission
must cover the full observation, including the subsequent Auto handback.

The four images configure the BRAM comparator with `93`, write `31` to
its control register, and acknowledge with bit `10`. ITE documents this
pattern on the IT81202 as a host-write address match and a fixed high-level
interrupt. If the same definition applies here, descriptor staging and
EC-local stores cannot trigger command publication, and clearing the match
flag removes the IRQ source. The Blade images use NDS32. The IT81202 is
RISC-V and has a different chip ID layout. An exact-chip definition or
equivalent evidence is still needed for both the host-write condition and
interrupt drainage. [ITE IT81202 specification, pp. 186, 198, 434–435](https://www.ite.com.tw/upload/2024_01_23/6_20240123162204gq4mc18f22.pdf)
· [Pinned Chromium IT8320 configuration](https://chromium.googlesource.com/chromiumos/platform/ec/+/82d2822/chip/it83xx/config_chip_it8320.h)

The scheduling model needs only one residual notification after D0
copies its result. From an initially quiet dispatcher it explores 281
states and accepts 10 fence states, with no false read success. In the
failure case the residual callback starts before the A0 publication and
loads the opcode afterward. A0's own pending event then starts an empty
callback. After the host accepts the correct A0 data and publishes A1,
that empty callback's unconditional outer clear erases A1. The observed
command and status are both zero and Auto never applies. This is a
conditional interleaving. Bounding residual delivery to one event does
not establish quiescence.

The exact keyboard images for 0423 (1.02.00), 0484 (1.04.00), 0509
(1.01.00), and 0529 (1.07.00) send both named duty commands, `0D/0D` and
`0D/8D`, to the unsupported handler with status `05`. The separate raw
bridge still requires bank ownership and an independently retained HID
handback path.

### Keyboard bridge scheduling and bus completion

The keyboard bus engine has two direct call sites in each pinned image,
one from the read helper and one from the write helper. The three older
images also have a single-byte write wrapper. Its 24 direct callers per
image use fixed indexes `00/01/02/FD/FE`. The whole-image direct branch
scan found no extra tail branches or literal Thumb function pointer to
the engine. Computed entry and other physical bus controllers remain
outside that result.

| Model | Timer vector / call to HID dispatcher | Foreground call to thermal worker | Shared frame buffer | Write STOP request / result store |
| --- | --- | --- | --- | --- |
| RZ09-0423 | `B5CC / B69C` | `8F46 → 2544` | `10000324` | `3146 / 314C` |
| RZ09-0484 | `B858 / B928` | `903A → 2544` | `10000326` | `31EA / 31F0` |
| RZ09-0509 | `BACC / BBA4` | `91EE → 2544` | `1000032E` | `322A / 3230` |
| RZ09-0529 | `35D8 / 3688` | `6B66 → 4E0C` | `10000200` | `25D0 / 25D4` |

The timer dispatches the synchronous HID bridge. The foreground worker
stages ordinary fan traffic in the same buffer and length fields before
calling the bus engine. Neither helper checks a busy flag or saves the
previous frame before staging. If the timer can interrupt a foreground
call between staging and engine entry, even a bridge read can replace
the staged frame and lengths before the worker submits them. The trace
identifies the window, while runtime interrupt masking and scheduling
still need to be established. Serializing host HID requests alone does
not settle this firmware-side overlap.

The write path stores success `0C` immediately after requesting the
I²C STOP condition. The engine returns the software result and disables
its IRQ without polling the bus. Its local timeout stores `0B` and
disables that IRQ without an explicit STOP or peripheral reset in that
path. The HID response supplies neither wire-idle evidence nor EC
callback completion. Keep a timeout ambiguous. [NXP I²C specification, §3.1.4](https://www.nxp.com/docs/en/user-guide/UM10204.pdf)

### Candidate direct read through Super I/O

All four exact EC carriers contain the same managed flasher, SHA-256
`3de34bf3f5e57e0c11e76f45b773642ae3816f4a645040cf3b0602dcd30e79ed`.
Its `Embedded_Controller_Get_Ram_Byte` method (RVA `3CC4`) selects
16-bit addresses through the Super I/O depth-two index and data
registers, then reads the selected byte. This method publishes no BRAM
command and calls no keyboard transport. Its only direct caller in the
retained assembly is the separate SPI ID helper at `392C`, which uses
it at indirect flash register `103F`. The reviewed managed call
inventory does not reach that helper from the updater entry. The
method establishes a vendor transport definition and register example,
with normal-mode availability still open.

| Model | Shared flasher block | EC store of `C3` at `F0200D` |
| --- | ---: | --- |
| RZ09-0423 | 25 | `CF78` |
| RZ09-0484 | 28 | `DCD0` |
| RZ09-0509 | 12 | `E3D2` |
| RZ09-0529 | 7 | `E692` |

Each EC's initialization code sets the low two bits of `F0200D`.
Chromium's IT83xx code names offset `0D` SPCTRL1. ITE's IT81202 manual
and the exact Razer helper independently agree on the depth-two
register layout. The manual identifies SPCTRL1 bits 1:0 as memory-snoop
permission and describes external-memory access through this path.
That helps explain the firmware setting without proving that every
16-bit address resolves as expected on the installed Blade controller.
[Chromium IT83xx LPC code](https://chromium.googlesource.com/chromiumos/platform/ec/+/08f5a1e6fc2c9467230444ac9b582dcf4d9f0068/chip/it83xx/lpc.c)
· [ITE IT81202 specification, pp. 78, 433, 533–534](https://www.ite.com.tw/upload/2024_01_23/6_20240123162204gq4mc18f22.pdf)

The next proof for this route is the installed controller's ordinary
host decode and its 16-bit address binding to `F01808`. A read-only
comparison needs exact device and firmware identity, a known-answer
control, independent mapped duty and cycle comparisons, and a third
channel observation that can distinguish a real duty value from a
constant, a shadow, or a wrong address. The nested selectors also need
ownership through every read, including Auto handback. If those checks
pass, Stop can use independent actual PWM without submitting D0.
The A1 publication and verified Auto handback still require their
separate completion proof.

The EC can temporarily close Super I/O host access during PNPCFG
programming. The general table writers are `AC06/B084/B4D0/B31A` on
0423/0484/0509/0529. They set `F01202` bit 0 while programming and clear
it at exit. The direct callers traced on these images are startup and
power-sequence paths. For example, KI8110's event-6 dispatcher reaches
the writer via power states `06/0A/0B`, while its steady state dispatch
does not reach that writer in the reviewed call graph. The emitted
tables use no depth-two index or data registers. The secondary writers
also avoid those registers and have no direct or literal-pointer caller
in the pinned images. These findings narrow steady-state contention.
They leave the need to exclude lifecycle crossings and competing host
access during the entire nested selector operation. [Chromium EC2I register access](https://chromium.googlesource.com/chromiumos/platform/ec/+/08f5a1e6fc2c9467230444ac9b582dcf4d9f0068/chip/it83xx/ec2i.c)

The four images also copy actual PWM3 to RAM `80536` during ordinary
feedback. That copy is skipped in FE, and no mapped descriptor exposes
it. The mapped third tachometer is separate. Its producer can return
zero after missing or disabled feedback, so neither field substitutes
for actual PWM readback during Stop.

All four exact images can now be inspected with the
[offline firmware CLI and register explorer](../tools/firmware/README.md).
The profiles decode all mapped entries and annotate the staged duty,
actual duty, tachometer, cycle and restore fields separately.

### EC-native USB selector candidate

The pinned images also contain a separate USB device descriptor for
`1532:F003`, device release `0100`, at `2AE00/2B258/2A0E4/2A874`
on 0423/0484/0509/0529 respectively. This is distinct from each model's
keyboard MCU product ID. The KI890 descriptor calls it Razer EC and
declares two HID interfaces and one vendor interface.

On KI890 the ordinary event table reaches USB state handling through
`1D63A → 69A8`. The precheck at `C6A4` returns a constant `FF`.
The pending flag `80CF` and zero state `80D4` select `22424`.
With `80D6` zero, that routine reaches the initializer at
`222E0 → 7C06 → 79A0` and sets `80D6` to one. Nonzero `80D4` or an
`80D6` value of one takes the `7B10` route instead. The inspected path
does not identify a board strap, a host-visible enable switch, or the
state of those bytes during a retail boot. The USB SET_REPORT handler
dispatches the 90-byte report before scheduling the status stage.
The EC-local `0D/02` handler stores the selector at `8035F` and a fresh
`0D/82` reads that byte directly. This path does not use the keyboard
MCU's I²C staging or selector cache. The separate `0D/84` table reads
speed or configuration RAM and supplies no actual third PWM reading.

An exact-board enumeration is still needed to establish whether the
EC USB endpoint is connected and visible to the host. The selector
round trip would also leave the captured override and staged duties to
restore through separately reviewed operations. The signed KI890 BIOS
2.09 carrier (SHA-256
`ea6b01417b8b868a60c7f85dc194aaa8e8ec0d117dedaa6f3689ff09fbde9741`)
identifies RZ09-0484 and SSID `1A583007`. A compressed section could
only be decoded in part. The recovered BIOS bodies and decoded prefix
contained no matching device identity, so this inspection does not
settle host visibility. [Razer KI890 BIOS 2.09](https://mysupport.razer.com/app/answers/detail/a_id/14221/kw/RZ09%2Baudio%2Bdriver)

Seven full Linux USB inventories from six RZ09-0423 hosts list the
model's keyboard device `1532:028B`. None lists `1532:F003`. The
matching DMI records report EC revisions 1.02 and 1.03. Two RZ09-0509
hosts likewise list `1532:02B8` and no `1532:F003`, both at EC revision
1.01. These are negative observations for normal enumeration at those
revisions. The decoded DI780 1.05 and KI8100 1.02 images remain untested
on their respective boards. [RZ09-0423 USB inventories](https://github.com/linuxhw/LsUSB/tree/3b1ee387e93df2a09ebc82df7a3cd0b2298a5265/Notebook/Razer/Blade/Blade%2017%20-%20RZ09-0423)
· [Pinned DMI records](https://github.com/linuxhw/DMI/tree/57b21bc6fdd863380e99c228c22aafcd1509528c/Notebook/Razer/Blade)
· [RZ09-0509 probe 46be4bc579](https://linux-hardware.org/?probe=46be4bc579&log=lsusb)
· [RZ09-0509 probe d0e4380367](https://linux-hardware.org/?probe=d0e4380367&log=lsusb)

The public RZ09-0529 owner report includes USB descriptors for the
laptop and cooling pad. That excerpt cannot establish whether the EC
endpoint enumerates. [OpenRazer RZ09-0529 report](https://github.com/openrazer/openrazer/issues/2488)

### Three-channel workflow and journal

The shared duty coordinator now stages all three channels before
entering FE and requires observed actual duty for all three. The journal
binds the third target at creation and rejects missing third-stage,
third-duty or third-tach observations. Auto restores the captured
selector, override and changed staging bytes in that order. Simulation
covers the round trip and fencing when readback is lost after submission.
The live guardian still admits only two-channel contracts. Connecting
the three-channel workflow requires independent third-duty readback and
a completion contract that protects Auto through handback.

## Seven models with missing receiver evidence

| Model | New evidence | Remaining EC evidence |
| --- | --- | --- |
| RZ09-0300 | Two exact DA530 selectors, SSID `1A582004`, in the signed Intel ME carriers. | D3 BIOS and the additional Intel ME packages have yielded no laptop EC implementation. |
| RZ09-0301 | Official keyboard firmware `1.00.01` for USB `1532:0245`, decoded and traced through the thermal I²C peer. | Receiver zero handling, fresh actual duty, tach provenance, and verified Auto. |
| RZ09-0328 | Razer-signed DA550 BIOS 1.03 with SSIDs `1A582011` and `1A58200C`. The carrier includes BIOS and Intel ME. | An exact EC implementation or equivalent vendor contract. |
| RZ09-0351 | Two signed OL510 selectors pin SSID `1A58200D`. They select Intel ME payloads. | An attributable, usable OL510 EC image. The exact-board dump mentioned in the repair post is restricted and comes from a board reported unable to start. |
| RZ09-0370 | Two older Razer-signed updaters contain the same P1106 BIOS image. Exact selectors include SSIDs `1A582013` and `1A582014`. | Both packages select BIOS only. The EC side of the BIOS mailbox remains unidentified. |
| RZ09-0482 | Microsoft-signed public catalog capsule for PI490 BIOS 1.06, with verified INF and ROM membership. | The older image retains the conditional OEM fan hook and the generic EC mailbox. The [keyboard MCU bridge](fan-stop-pi490-mcu-bridge-review.md) still ends at the receiver. |
| RZ09-0508 | Exact Synapse product 694 advertises firmware through the already reviewed BIOS 1.06 article. | PI4100's EC implementation and its fan contract. |

The RZ09-0301 keyboard carrier has SHA-256
`58f880cc46a64379131e7edc3d1a8c63418dc2989179d3a514d6d79ea38e49e5`.
The 3,673 Intel HEX records yield 58,892 bytes, SHA-256
`2b0073e46d07119027a44136f9cc9ca25d8ebd072418193e9a6f36c267720c98`.
The keyboard parser's class `0D` handler at `9C42` stages zone 1/2 targets
and forwards their raw bytes to wire address `B0`, registers `1B/1C`.
This includes zero. The mode worker writes the staged profile to `1A`,
commit value 5 to `0A`, then the staged mode to `36`. The current-speed
command `0D/88` at `9E44` reads a separate cache populated by a two-byte
I²C read of register `08` at `2960`. The target GET returns the same
cache that target SET populates. Current-speed GET itself does not initiate
a fresh transfer. [Razer keyboard updater](https://mysupport.razer.com/app/answers/detail/a_id/1967/)
· [Exact Synapse command definitions](https://apps.razer.com/synapse/products/581/mw/rzDevice25.f668146da6ee1d0b3b6c.js)

Exact product 624/669/694 Synapse resources expose ordinary fan mode and
speed paths. The shared duty method definitions do not establish a working
exact-model duty interface. In particular, the PI490 MCU's named duty
command is rejected. [Product 624](https://apps.razer.com/synapse/products/624/mw/installer-manifest.json)
· [Product 669](https://apps.razer.com/synapse/products/669/mw/installer-manifest.json)
· [Product 694](https://apps.razer.com/synapse/products/694/mw/manifest.json)

Package sources include the
[older PI410/PI411 updater](https://dl.razerzone.com/drivers/Razer%20Blade%2014%22%20%282021%29%20%7C%20RZ09-0370/RazerUpdater_v1.1.1.3_PI410_PI411-BIOSv0106v2.exe),
[PI490 catalog capsule](https://www.catalog.update.microsoft.com/ScopedViewInline.aspx?updateid=7dba8797-85b4-4d79-91b7-5f524c75ad21),
[RZ09-03519 downloads](https://mysupport.razer.com/app/answers/detail/a_id/5655/),
and [OL510 repair report](https://vlab.su/viewtopic.php?f=35&p=1129927&t=151751).

### Receiver acquisition and PI490 speed feedback

RZ09-0300 has a separate original 8 MiB ITE8390 flash listing for the
DA530 board in `03009E76-R3U1`. It is access-restricted and the donor did
not POST. On RZ09-0301, the original CH530 FCS V3.0 paired reads identify
the two physical flash packages. A second listing offers an 8 MiB image,
but access and donor provenance are missing, and the 8 MiB package still
needs to be tied to the receiver. CH530 is the first acquisition target
because its exact keyboard transport already carries zero to the receiver.
[DA530 original read](https://vinafix.com/threads/razer-rz09-03009e76-r3u1.44603/)
· [CH530 original pair](https://vinafix.com/threads/bios-rz09-0301-9th-generation.38915/)
· [Second CH530 listing](https://vinafix.com/threads/rz09-0301-ch530-mb-fsc-v3-0.41761/)

Razer's public index lists BIOS and keyboard updates for RZ09-0301.
The recheck identified no separate receiver package or additional
public receiver bytes for the seven models.
[Razer firmware index](https://mysupport.razer.com/app/answers/detail/a_id/4166/kw/Nari%20downloads)
· [RZ09-0301 support and downloads](https://mysupport.razer.com/app/answers/detail/a_id/3683/)

DA550 MP V4.0 reports identify IT8390E. The superficially similar CH551
original pair came from RZ09-0330. A separate OL510 PVT V1.0 board photo
records distinct BIOS and EC flash locations on the owner's unit.
The DA550 and OL510 searches still need attributable receiver bytes.
[DA550 board reports](https://vinafix.com/threads/razer-da550-mb.46075/page-2)
· [CH551 donor report](https://vinafix.com/threads/rz09-0328-ch551-mb.41122/)
· [OL510 owner photo and report](https://winraid.level1techs.com/t/looking-for-ec-firmware-for-a-razor-blade-15-2020-base-rz09-03519/107897)

PI490 keyboard firmware 1.04.00 feeds HID current speed (`0D/88`)
from two bytes read at wire address `B0`, starting at register `08`.
The worker updates its cache only on a successful transfer, and the
HID getter neither forces that transfer nor reports the cache age.
Synapse's Auto UI scales those values by 100 RPM per count. The
receiver's tach producer and actual PWM remain unverified. The
PI490 and PI4100 native firmware-version exports read the EC version
from the Windows BIOS registry. They provide no independent EC read.
The reviewed PI410/PI411 native library lacks that export.
[PI490 keyboard updater](https://mysupport.razer.com/app/answers/detail/a_id/13877/)
· [Exact device class](https://apps.razer.com/synapse/products/669/mw/rzDevice25.0b7f4b3291f821b73ff6.js)
· [Synapse policy](https://apps.razer.com/synapse/products/669/mw/6737.5791b4f2132a8ffb5846.js)

The older DA550 carrier has SHA-256
`02e23deb63ef703dbdb5349e33590e7fa9fc9a3eb426a4d63e3fc3f6285e3d20`.
All 15 NSIS blocks were accounted for. The batch selects a 16 MiB
`Dana5_0103.bin` and an Intel ME image. The DSDT matches BIOS 1.06, and
the 55 new body hashes did not identify a receiver payload.
[Razer DA550 BIOS 1.03 carrier](https://dl.razerzone.com/drivers/Razer%20Blade%2015%E2%80%9D%20Base%20%282020%29%20%7C%20RZ09-0328x/RazerUpdater_v1.1.1.2_DA550-BIOSv0103.exe)

The public Microsoft catalog returned the previously examined PI490
BIOS 1.06 as the positive control. Exact capsule GUID searches and LVFS
stable and testing metadata yielded no new attributable AMD receiver
artifact. Eight signed exact-model Synapse host resources exposed
firmware-version and touchpad tooling. Their bounded static inventories
identified no receiver image. Razer's recovery service requires account
and serial binding and documents Windows recovery. These results bound
the public sources checked. [Microsoft Update Catalog](https://www.catalog.update.microsoft.com/Search.aspx?q=Razer%20Firmware)
· [Razer recovery service](https://mysupport.razer.com/app/answers/detail/a_id/6300/)

Detailed local proof and input manifests are under
`artifacts/model-expansion/bram3-followup/`,
`primary-reader-followup/`, `intel-missing-ec-followup/`, and
`amd-missing-ec-followup/`. Later proof and acquisition results are under
`bank-ownership-model/`, `bank-ownership-followup/`,
`intel-receiver-next/` and `amd-receiver-next/`. The interrupt census,
trigger documents, and most recent acquisition results are in
`reader-resolution-next/`, `trigger-resolution-next/`,
`intel-receiver-resolution/`, and `amd-receiver-resolution/` under the
same artifact root. `keyboard-bus-ownership/` retains the MCU byte
checks and the one-notification model. The reader proof hashes all four EC images,
checks the opcode paths and calls, and reproduces the ordering model.
The package work verified signatures or catalog membership and inspected
the decoded contents offline. Runtime admission remains open for these
eleven models.
