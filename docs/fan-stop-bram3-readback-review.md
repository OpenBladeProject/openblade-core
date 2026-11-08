# Alternate BRAM3 third-duty reader: exact-image review

Reviewed 2026-11-08 against `10caac37c5868ee8bbc4a54a392141facc5b5a34`.
This is static source evidence, not physical validation or runtime admission.
No updater, vendor assembly, driver, port operation, or hardware command was executed.

## Outcome

The exact firmware images resolve the missing **programmed 302/303 port base**
evidence for all four models, including RZ09-0509. Their legacy reader also
avoids the primary reader's delayed second-clear hazard. However, the complete
transaction is **not yet implementation-ready**: ordinary firmware callbacks
reuse its three address bytes independently of command `12`. A specific
interleaving can turn the intended third-duty read into a completed zero read
from another address. The remaining issue is descriptor ownership, not a
missing updater method name or a requirement for physical validation.

## Pinned inputs and port initialization

| Model / image | EC SHA-256 | SIO table | Initializer / EC2I writer |
|---|---|---|---|
|RZ09-0423 / DI780 1.05|`ada7830bcca91f0201d108386a4c4efde39197bd31719f810db4481fd0e58dea`|`253EC`|`AC92` / `AC06`|
|RZ09-0484 / KI890 1.07|`a1d2e6ebe7de811661d0d61fd9e4ae108f8103fd8d65b1af72996faa1beadde2`|`25B84`|`B110` / `B084`|
|RZ09-0509 / KI8100 1.02|`1d53c56d56581331c480bd12cb240b5657acc6ad5877711ffea2e4b8d7a073b1`|`28854`|`B55C` / `B4D0`|
|RZ09-0529 / KI8110 1.06|`1cee6e614bfb086946f691cbc551863d6f697495159d23ede505df49fa47752d`|`28FC8`|`B3A6` / `B31A`|

Offsets are zero-based offsets in each 262,144-byte image, also its mapped
firmware address. Instructions are NDS32 big-endian; table byte pairs are
register/value. Original carrier provenance is retained in
[the preceding readback review](fan-stop-blade18-readback-resolution.md).

Every initializer passes its table and length `54` to an EC2I writer. That
writer enables `F01202/04/05` bit 0, selects EC2I index access using `F01200=0`,
sends each table register through `F01201`, waits for `F01205` bit 2 to clear,
selects data access using `F01200=1`, sends the table value, waits again, then
disables its bridge bits. Do not rely only on the decompiler here: it omits
several index writes that the retained instructions show explicitly.

The identical LDN `10` fragment is:

```text
07 10 60 03 61 02 62 03 63 00 F1 0F F2 10 F3 00 F4 00 30 01 F5 00
```

It selects LDN `10`, programs first base `0302`, second base `0300`, and
activates the logical device. In the exact DI780/KI890/KI8110 host metadata,
`RWIo..cctor` assigns `MailBox_Index=770` and `MailBox_Data=771`; the exact
`ReadSRAMViaBRAM3` IL uses those fields. Only those initializer assignments
were found for these fields. Thus the firmware tables independently corroborate
the host's `302/303` index/data mapping, and KI8100 contains the same configured
bases and reader. This is programmed firmware configuration; it does not prove
that a particular live host has not changed its decode or that arbitrary BIOS
versions share it. The secondary `300/301` window is not substituted here.

For DI780, reset/startup caller `3000` calls `AE2A` at `3062`, which calls the
initializer at `AE5C`; state-transition initializer `CF66` calls it at `CFD6`.
This is active initialization code, not just unused constants. The table and
call instructions of each model are retained in the local proof artifacts.

## Reader and completion semantics

| Model | Raw reader | Dispatcher | Command 90 reader call | Final command clear |
|---|---|---|---|---|
|0423|`1162C`|`11688`|`11C52`|`11DDA`|
|0484|`12350`|`123AC`|`12976`|`12AFE`|
|0509|`128E4`|`12940`|`12F16`|`130A6`|
|0529|`12C34`|`12C90`|`132B0`|`13440`|

The candidate fixed contract is: stage indices `13=F0`, `14=18`, `15=08`;
publish `12=90` once; poll `12` until zero; read index `1C`. The firmware
assembles a 24-bit big-endian address, reads one byte, copies it to `F0221C`,
clears the address fields, and finally clears command `F02212`. The legacy
zero-command branch goes directly to the return, after the clear instruction.
An invocation that observed zero cannot later erase a newly published command.
Extra serialized invocations therefore do not recreate the primary wrapper's
late-clear problem. This does not prove exclusion of independent descriptor
writers or arbitrary interrupt reentry.

The dispatcher is called from an ordinary scheduled callback. For example,
0423 event 6 (`1D02C`, pointer at `2A828`) calls `1D014`, which calls `BA44`,
which calls the dispatcher at `BA74`. Consequently this path does not require
the primary mailbox command-byte interrupt trigger to invoke the reader.

## Concrete descriptor-ownership gap

All four images have a non-dispatch callback path that ultimately clears
`F02213..18`, including all three staged address bytes. These paths are separate
from the cleanup following command 90:

|Model|Event 6 call / destination|Callback call / destination|Final call / ordinary cleanup|
|---|---|---|---|
|0423|`1D102 -> BE7C`|`BF12 -> 16116`|`161DE -> 17132`|
|0484|`1D742 -> C930`|`C9C6 -> 16634`|`166FC -> 17698`|
|0509|`1C28A -> CE68`|`CEFE -> 2752A`|`275F2 -> 24862`|
|0529|`1C9C8 -> CD98`|`CE32 -> 27D3A`|`27E02 -> 24FA2`|

For 0423, the callback checks pending flag `80439` bit 3 and then calls
`16116`; it does not acquire command `12` or exclude host staging. That flag is
set by legacy command `21`, subcommand 3. The same descriptor helpers are also
used by the model's other configuration/profile paths. A pending request from
a prior controller is not ruled out merely by current process absence.
This is a conditional code path, not a claim that it executes on every poll.

The ordinary cleanup is byte-identical across these images (relocated):

```text
46000f02 46300080 8420 8440 8480
10100214 10100215 10100216 10100217 10100218
12218220 14418111 1020021b 10100213 dd9e
```

A concrete possible schedule is:

1. Host observes command 0 and stages `13=F0`.
2. An already pending ordinary callback clears `13..15` without testing command 12.
3. Host stages `14=18`, `15=08`, then publishes `12=90`.
4. The next reader reads `001808`, copies its byte, cleans the descriptor, and clears command 12.

The byte at exact image address `001808` is **00 in all four images**. Thus the
host can receive a normal completed zero even though it did not read `F01808`.
This counterexample assumes the pending callback's normal success branch is
reached; it demonstrates why the presently reviewed contract cannot establish
address integrity. It is not a captured hardware failure. A descriptor readback
before publication only narrows this race: cleanup can run after verification.
Command zero, a duty range check, a repeated zero, or a timeout do not provide
an ownership proof. After the command, normal cleanup has erased the address,
so post-completion descriptor readback cannot recover the consumed address.

A separate delayed cleanup also exists (0423 `17448 -> 17166`): when counter
`8044E` expires it tests command zero and clears address bytes 14/15. Its
check-before-clear interval is another staging window. This is additional
support, not needed for the ordinary-callback counterexample above.

## Guard refinement: scoped FE remains a candidate

The schedule above is **not established as reachable under a fully admitted,
clean FE session**. It must not be presented as an unconditional flaw of a
scoped reader. Further exact-image checks narrow the conditions:

- All four reset stubs clear `80000..81FFF` at `508C..50A2`, which includes
  pending flag `80439`, delayed-cleanup counter `8044E`, and thermal state.
  The shared instruction bytes are
  `441020004e12000a460000808440ae809c019e494e13fffd`.
- Each image has four direct stores to `80439`: clear bit 0, clear bit 3,
  legacy command `21` setting one of bits 0..3, and the thermal worker clearing
  bit 2. The sole direct bit-3 setter is command `21` / subcommand `3`.
  FE/Auto processing does not independently set this bit in these direct paths.
  This is an exact direct-store inventory plus mapped-alias review, not a
  whole-firmware proof excluding every indirect store.
- The thermal workers are `14FFC`, `15518`, `2642E`, `26B92`, respectively.
  Their enabled FE branch performs duty maintenance and returns before the
  profile/table helpers that reuse the descriptor. Thus FE excludes that
  particular ordinary reload path. The FE permission-bit failure branch resets
  the selector to Auto; fresh selector/ownership checks remain necessary.
- Auto/profile processing invokes those helpers when the current selector
  differs from the loaded selector, when `80439` bit 2 requests a reload, or
  when `8034E == 1` and `8034D` bit 0 has cleared. Stable selector alone does
  not establish the complete no-reload condition. FE does not itself set the
  loaded-selector marker `8034F`; its return occurs before that assignment.
- The delayed cleanup requires nonzero `8044E`. The direct setters found are
  legacy storage-command branches; ordinary cleanup clears it. A known-zero
  counter plus exclusion of those commands is a useful candidate invariant.

All 2,048 mapped descriptors per image were scanned. Mapped bank/index `0:1A`
points to selector `8035F`; `0:36` points to manual-state byte `8034D`; every
bank's `06` points to enable state `802E3`. There are no exact-address mapped
aliases for `80439`, `8044E`, `8034E`, `8034F`, timed pending words `801C8` /
`801CC`, or the staged descriptor `F02213..15`. The reader cannot bootstrap a
trusted read of its own exclusion guards merely by using the same unproven
reader to read them.

A known-clean reset followed by continuous exclusion of descriptor-using host
commands and lifecycle/profile transitions could therefore support a narrower
invariant. Ordinary service acquisition does not itself demonstrate that
history. An already-running callback must also have finished before host
staging; a guard can have been consumed before the callback returns.

### Does observed FE prove callback drainage?

No such implication was established. The exact DI780 event-6 order is:

1. `1D014 -> BA44`: legacy command dispatcher, then an optional immediate
   thermal-worker invocation guarded by `8033F` bit 0.
2. Pending word `801C0`: `BD04 -> 14FFC`, the timed thermal worker.
3. Pending word `801C8`: `BE7C`, including pending `80439` bit-3 processing.
4. Pending word `801CC`: `BF36 -> 17448`, the delayed descriptor cleanup.

The thermal worker can therefore run **before** pending descriptor cleanup.
The top-level scheduler (`1D22C`, table load `1D23E`) scans indices 0..31 and
runs event 6 before primary-mailbox event 13 only when event 6 is actually
pending. It can skip event 6 and execute event 13. Within event 6, the timed
callback guards can independently skip cleanup even when the thermal worker
runs. The equivalent worker and event-6 bodies for all four images are retained
in `*-fe-guard-review.txt`.

A fresh primary observation of selector FE and first/second actual duty zero
thus does not, by itself, certify that pending descriptor users drained. The
actual duties may also have already been zero. This rejects that particular
proposed proof, not every possible FE-only admission strategy. A typed reader
used only after independently established clean FE ownership remains a concrete
research candidate; baseline and post-Auto third-duty reads would need their
own exclusion argument. No additional firmware command or guessed waiting
period is authorized as a reservation or drainage mechanism.

## Bounded implementation boundary and next evidence

Do not implement this candidate as a fallback after any uncertain primary
reader operation. No generic address, command `91`, storage commands, SIO
configuration writes, or firmware reservation command is admitted here.
A future fixed reader requires exact-model proof of an admission/ownership
condition that excludes these ordinary descriptor writers for the full staging,
publication, and reader interval. That condition must cover a callback already
in progress; observing a flag clear once is insufficient by itself. Alternatively,
an independently reviewed transport/firmware command with an immutable fixed
address can avoid this descriptor sharing. Neither solution is established by
this review.

Once ownership is established, the service would still need one serialized,
bounded transaction; fresh device/session/controller/power admission; success
only after confirmed completion and result read; and stop-on-ambiguity with no
replay, alternative command, or guessed zero. The read would be one duty sample,
not an atomic three-fan snapshot and not proof of physical fan stop.

## Reproducible offline checks

Ignored artifacts are under `artifacts/model-expansion/bram3-readback/`:

- `verify.py` hashes all four exact images, decodes and asserts each complete
  LDN10 configuration, finds non-dispatch callback paths, and emits exact-byte
  instruction spans in `*-verified-proof.txt` plus `verified.json`.
- `inspect.py` enumerates decompiled descriptor writers and caller context;
  this is a discovery aid, not a complete indirect-store exclusion proof.
- `guard_review.py` checks the shared reset-clear bytes, inventories direct guard
  stores, and scans all 8,192 mapped descriptors for guard aliases.
- `counterexample.py` replays the conditional staging schedule against each
  exact image and writes `counterexample.json`; all four return the wrong zero.

The image, counterexample and guard-review scripts passed. Decompiler call graphs alone are not treated
as exhaustive: the material call sites, reader, cleanup and table bytes were
checked against retained disassembly and pinned image bytes. No physical
behavior, timing bound, interrupt exclusion, or blanket firmware-version
compatibility is inferred from these offline checks.
