# Blade 17/18 third-duty read: a bounded completion barrier

Reviewed 2026-11-08 against the four exact images below. The D0 early-clear
problem has a source-backed, fail-closed solution: treat the fixed D0 read and
one fresh-status mapped read as **one compound observation**. D0's first zero
is an internal milestone. Its value becomes a completed observation only when
the mapped phase has independently completed. A lost mapped phase fails the
compound operation; it is never retried.

This resolves the additional outstanding D0 wrapper clear under the existing
isolated, stable mailbox contract. It does not establish universal exclusion
of firmware/OS/third-party port clients, guarantee delivery of every hardware
notification, or supply a cooling lease. Three-channel ownership, restoration,
typed implementation and software failure tests remain separate work. No
hardware, updater, driver or installation was executed for this review.

## Exact inputs and reproducibility

The carrier/source provenance remains in
[the original review](fan-stop-blade18-source-findings.md#exact-inputs). This
review independently re-hashed the extracted 262,144-byte images. Instructions
use NDS32 big-endian instruction bytes; descriptor/pointer data are little
endian. Code addresses below are base-zero file offsets; `F01808` etc. are EC
runtime data addresses, not Windows addresses.

| Model / image | EC SHA-256 |
|---|---|
| RZ09-0423 / DI780 EC 1.05 | `ada7830bcca91f0201d108386a4c4efde39197bd31719f810db4481fd0e58dea` |
| RZ09-0484 / KI890 EC 1.07 | `a1d2e6ebe7de811661d0d61fd9e4ae108f8103fd8d65b1af72996faa1beadde2` |
| RZ09-0509 / KI8100 EC 1.02 | `1d53c56d56581331c480bd12cb240b5657acc6ad5877711ffea2e4b8d7a073b1` |
| RZ09-0529 / KI8110 EC 1.06 | `1cee6e614bfb086946f691cbc551863d6f697495159d23ede505df49fa47752d` |

Ignored local evidence lives at `artifacts/model-expansion/readback-resolution/`:
`analyze_readers.py` verifies those hashes, emits original bytes beside the
existing Ghidra instructions, inspects every descriptor and records call
ancestry/pointer locations. Run it from the repository root. Its outputs are
`0423-proof.txt` through `0529-proof.txt` and `readers.json`.
`check_interleavings.py` emits `interleavings.json`; the model is described
below. These scripts open only files and create investigation artifacts.

## Fixed raw read and the outstanding tail

Use only primary index/data ports `300/301`, already established by each
exact carrier's `WriteRzEcMbox` / `ReadRzEcMbox` definitions. The raw command
semantics are established by the exact firmware, not by an observed updater
call: the retained host IL does not establish actual vendor use of D0.

The only proposed raw operation is read one byte from **`00F01808`**, the third
actual PWM. Set mailbox count `92=01`, address `9C..9F=08,18,F0,00`, and publish
`93=D0` last. These are primary mailbox address bytes, distinct from mapped
bank-zero logical register `9C` (third staged duty). Do not expose arbitrary
addresses, lengths, D1, secondary mailbox writes or raw port operations.

| Model | Count check | Address assembly / byte-copy loop | D0 inner clear | Wrapper final clear |
|---|---|---|---|---|
|0423|`207C..2084`|`209A..20DC`|`2094`|`115E8`|
|0484|`206C..2074`|`208A..20CC`|`2084`|`1230C`|
|0509|`206C..2074`|`208A..20CC`|`2084`|`128A0`|
|0529|`206C..2074`|`208A..20CC`|`2084`|`12BF0`|

The initial comparison permits count 0..32. Count zero copies nothing, and an
out-of-range count also reaches status `80` without copying. Therefore the
typed fixed count of one, intact request fields and successful port operations
are necessary. The loop rereads count; its initial bound is not protection
against a concurrent writer. Address assembly shifts high bytes from `9F`
through `9C`; `lbi.bi` reads the addressed byte and `sbi.bi` publishes it to
`F022A0`. Status `94=80` and the inner command clear occur after that copy.

After the inner clear there are no further D0 status, result, address or count
writes: `pop25 s4,8` returns to the wrapper, which executes `sethi F02`,
`movi 0`, the final `sbi [F02293]`, and `pop25`. The event callback then returns.
Thus saving the provisional result and preparing the mapped descriptor cannot
be corrupted by the remaining D0 instructions. The command publication itself
can still be erased by the remaining clear; that is what the barrier detects.

## One compound observation

Retain the existing exact-device, firmware-source, controller, session, power,
ownership, intact-port-result and deadline admission. Serialize the entire
compound operation, including both publications, under the same owner and
journal intent. Admission callbacks must not make nested mailbox requests.

1. Start only from the existing known completed/idle state; reset status and
   stage the fixed D0 descriptor, then publish D0 once. An uncertain native
   outcome, cancellation or timeout stops the operation without another
   command.
2. Observe D0 command zero and status `80`; save `A0` privately as provisional
   third-duty data. This is not a public successful read or a usable fan-state
   observation yet.
3. Reset status `94=00` with a confirmed operation. Stage mapped bank `90=00`,
   index `91=B2`, count `92=01`; publish `93=A0` exactly once. This phase is
   part of the original compound read, not recovery or replay of a failed read.
4. Poll command within the same bounded operation. Require command `93=00`
   and a fresh status `94=80`. If status is still zero, the fence may have been
   erased; reject and fence further commands. Never republish it or switch to
   another reader. Port failure, unexpected command/state, loss of admission,
   cancellation or deadline likewise leaves the observation unusable.
5. Only after that acceptance may the saved raw duty and fresh mapped cycle
   become the completed observation. Read the cycle payload before another
   transaction; a zero cycle is invalid for duty interpretation. The two
   values are ordered observations, not an atomic snapshot.

The fixed fence register has independent exact-image evidence:

| Model | Bank-pointer table | Bank-zero B2 descriptor | Exact descriptor bytes |
|---|---|---|---|
|0423|`2CD94`|`2A198`|`01 00 00 00 41 18 F0 00`|
|0484|`2D1EC`|`2A644`|`01 00 00 00 41 18 F0 00`|
|0509|`30094`|`2FCE8`|`01 00 00 00 41 18 F0 00`|
|0529|`30828`|`30478`|`01 00 00 00 41 18 F0 00`|

The descriptor is metadata 1, pointer `F01841`: the live shared PWM cycle,
already used for first/second duty. All 2,048 descriptors per image were
checked; none points to actual third PWM `F01808`, command `F02293` or status
`F02294`. No staged-duty alias was promoted to actual duty.

## Why the fresh status closes this particular race

After the D0 inner clear, the old invocation cannot set status again. A reset
status can become `80` from the fixed A0 phase only after the event dispatcher
has returned from the old wrapper and begun the new handler. Mapped A0 sets
status before copying, but leaves command nonzero until its wrapper clears it
after the copy. Therefore **fresh status 80 plus command zero** establishes both
that the old D0 wrapper finished and that the mapped read finished. Status 80
alone, first zero alone, repeated zero or any guessed elapsed delay does not.

If D0's trailing clear erases A0, a pending event can still call the handler
with zero. Crucially, that path does not set status:

| Model | Command load / zero branch | Zero-command destination |
|---|---|---|
|0423|`2042 / 2046`|`2352: pop25 s4,8`|
|0484|`2042 / 2046`|`2354: pop25 s4,8`|
|0509|`2042 / 2046`|`2354: pop25 s4,8`|
|0529|`2042 / 2046`|`23B8: pop25 s4,8`|

Its outer wrapper may clear command again, but status stays zero, so the
compound reader fails closed. Some unsupported **nonzero** commands reach a
status-80 store before rejection. Consequently the proof requires the fixed
opcode and sole-writer/invariant checks; `00/80` is not a general owner token.

## Dispatch, IRQ acknowledgment and other status writers

Both raw and mapped commands run through the same nonreentrant main-loop event
callback. The pending flag is cleared before invoking its function pointer;
the ISR only sets that flag and acknowledges the interrupt.

| Model | IRQ pending setter | Event table / slot 13 | Callback / wrapper | Scheduler table load |
|---|---|---|---|---|
|0423|`B360`|`2A810 / 2A844`|`C11C / 115DC`|`1D23E`|
|0484|`BA18`|`2AC68 / 2AC9C`|`C668 / 12300`|`1D87E`|
|0509|`BEE8`|`29AF4 / 29B28`|`CB9E / 12894`|`1C3C6`|
|0529|`C1A0`|`2A284 / 2A2B8`|`CFC8 / 12BE4`|`1CB04`|

For example, KI8110 main loop `3084` calls dispatcher `1CAF2`; it clears
`8000D` at `1CB1E` and calls table slot 13 at `1CB26`. ISR `C1A0..C1C6` stores
one to `8000D`, ORs acknowledgment bit 7 at `F01124` and bit 4 at `F0201B`,
then returns. It does not call `203C`, write command or write status. The
initialization immediately before each wrapper sets `F02019=93`,
`F0201B=31`, enables `F01125` bit 7, and clears the command. This is the exact
source's command-trigger setup; no new host acknowledgment write is required.

The software pending byte coalesces repeated notifications while already one.
It is not a request counter. This review does not turn those configuration
bytes into a datasheet-level guarantee against every possible duplicate or
delayed hardware notification. An unrelated later empty callback could still
execute an unconditional wrapper clear, an underlying primary-mailbox
limitation also relevant to ordinary mapped operations. The new barrier proves
the *known outstanding D0 invocation* has finished; it does not claim a universal
hardware queue drain or relax the existing stable isolated-transaction boundary.

The modeled successful sequence assumes the configured host command publication
is the event source: D0's event is consumed before D0 executes, A0's event is
consumed before A0 executes, and EC-local command clears do not create another
host-write event. Descriptor/status writes do not publish another command.
If the A0 publication was erased, its pending zero callback may remain; the
failed compound operation fences all later commands, so there is no permitted
later write for that callback to erase.

All four images identify themselves as `ITE8320EC` at file offset `90`.
The public [Chromium EC ITE register header at commit 82d2822](https://chromium.googlesource.com/chromiumos/platform/ec/+/82d2822/chip/it83xx/registers.h)
independently defines general-control base `F02000`, BRAM base `F02200` and
I2BRAM interrupt 71, but omits general-control offsets `19/1B`. It therefore
does not close the hardware-trigger assumption. The downloaded local header's
SHA-256 is `e8251895319613e672f12d010aa5d8dea287d0ceee53f6d7a438348708029ec6`.
No exact-chip public definition of those two registers was located in the
bounded primary-source search. A returned IT82302 datasheet was not used to
infer IT8320 behavior.

The extraction inventories every literal status-offset store and its recovered
function ancestry. All recovered literal-offset `F02294` writers are in `203C` or synchronous
descendants reached by its other opcodes. There is no independent asynchronous
status writer among these recovered paths. DI780's additional `1352C`, `13670`
and `13780` families enter through `13794`, itself called from `203C`. The
apparently asynchronous halfword stores at `F740 / FF04 / 10606 / 108A8` use
base `80000`, so write **RAM 80294**, not mailbox `F02294`. Initialization's
bulk mailbox clear writes zero and cannot forge fresh status 80. Computed
arbitrary D1 or foreign-host stores are excluded by the typed ownership scope,
not by pretending the firmware lacks those capabilities.

## Offline race verification and limits

`check_interleavings.py` enumerates 48 reachable states for the reviewed D0
tail, host result save/status reset/staging/publication, event decode, mapped
status/copy/final clear and split host completion reads. It includes the
erased-command zero-opcode callback. Both success and fail-closed paths occur;
there are two accepted terminal states and zero premature accepts. It also
records explicit counterexamples for first-zero and repeated-zero shortcuts.

This finite abstraction proves ordering under its stated predicates. It is
not an NDS32 emulator, hardware timing test, exhaustive recovery of all indirect
firmware stores, or a physical result. In particular it does not inject resets,
foreign host writes or arbitrary extra hardware notifications. The parent
investigation independently models the barrier and the no-status-reset
negative control. Runtime implementation still needs failure handling for
each real port boundary and must retain the complete compound provenance.

## Alternative legacy one-byte reader

There is also a separate reader with a single final clear in every exact image:

| Model | Raw-byte reader | Call from command 90 | Address cleanup | Final clear of command 12 |
|---|---|---|---|---|
|0423|`1162C`|`11C52`|`11674`|`11DDA`|
|0484|`12350`|`12976`|`12398`|`12AFE`|
|0509|`128E4`|`12F16`|`1292C`|`130A6`|
|0529|`12C34`|`132B0`|`12C7C`|`13440`|

It assembles a 24-bit address from `F02213..15` most-significant byte first,
loads one byte, stores the result at `F0221C`, returns, clears address fields,
then clears `F02212` exactly once. Its zero-command dispatcher branch returns
without the final clear. It could avoid the D0 barrier entirely with a fixed
`F0,18,08` address and command `12=90`.

DI780, KI890 and KI8110 carrier host metadata independently contains
`RWIo.ReadSRAMViaBRAM3`, token `06000028`, RVA `23B8`: address at IL `0014..0054`,
command publication `0059..006F`, poll `0074..008A`, result `008C..009D`.
The class initializer assigns **302/303**, not 300/301. Exact KI8100 extracted
components did not contain that method name. These definitions establish a
concrete alternate source lead; they do not establish active vendor use of
this method or independently settle all four models' host-port mapping.
It remains a separately reviewed alternative, not an automatic fallback after
a failed compound D0 read. Neither generic legacy write command 91 nor its
storage/update families are part of the proposed reader.

## Implementation admission still required

- Expose only the exact-model fixed third-duty read. Keep raw addresses, D1
  and the alternate 302/303 transport outside production admission.
- Serialize and journal both publications as one observation. Preserve the
  provisional value privately; any uncertain port boundary, erased fence,
  cancellation or deadline must prevent success and further commands.
- Carry the third actual duty and the fence's fresh cycle into snapshot
  validation. Reject inconsistent cycle observations and out-of-range duties;
  the two reads are not an atomic snapshot.
- Extend all three channels through staging, measured-tach coast, ownership,
  durable checkpoints, guardian continuation and exact Auto restoration.
  Existing two-channel journals must retain their own interpretation.
- Verify success, every submission/failure boundary, stale status, erased
  fence, zero-opcode callbacks, caller loss and restoration in software before
  enabling these model contracts. Physical testing remains a separate status.
