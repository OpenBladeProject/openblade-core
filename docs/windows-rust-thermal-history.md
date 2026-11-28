# Thermal history and managed handback v1

This offline Windows migration slice retains the managed product selection, UI,
IPC versions and exact-device command admission. It adds no firmware command or
installation authority. Physical and installed-runtime validation remain separate.

## Grammar and bounds

The production grammar and deterministic engine are in the existing portable
openblade-domain crate, under its thermal-history feature. This feature uses
allocation, serde and SHA-256 with std::io error values; it calls no OS supplier.
The existing allocation-free policy core remains no_std when this feature is off.
The Windows host implements its protected Backend under the original owner writer.
WindowsNativeThermalHistory is the managed reader. Both use the exact UTF-8 vectors
in tools/openblade-windows-host/tests/data/thermal-history-v1.json.

Each raw JSON row is at most 16,384 bytes including its LF. Empty rows, duplicate
members, unknown typed fields, invalid original owner/request/caller identity,
invalid intent digests and contradictory chronology refuse admission. Typed
destinations are Balanced=1 and Performance=2. Progress is 0 Prepared, 1 first
intent, 2 first acknowledgement, 3 second intent, 4 second acknowledgement,
5 Confirmed, or 6 KnownNotSubmitted. Only 0->1->2->3->4->5 and 0->6 settle.
Confirmed retains its reviewed independent paired-read producer guarantee;
KnownNotSubmitted retains its separately positive original transport-retirement
guarantee. Neither grammar nor metadata recovery creates either guarantee.

Readers built with `openblade-domain/hyperboost-exit`
(`service-thermal-hyperboost-exit-v1`) and WindowsNativeThermalHistory also
admit the HyperBoost exit to a battery preset (destinations 3 and 4 only):
0, then 80 first restore intent, 81 first restore acknowledgement, 82 second
restore intent, 83 second restore acknowledgement, then 1->2->3->4->5. The
restore rows never settle: 83 continues only to 1, so a row cannot confirm
without its battery pair, and a restore row after the battery pair, on any
other destination, or without that feature is refused. The
`hyperBoostExitRows` vectors pin both readers.

Readers built with `openblade-domain/hyperboost` (`service-hyperboost-v1`)
and a WindowsNativeThermalHistory whose artifact names that capability also
admit owned HyperBoost rows: destination 100 with a null requested override,
on the pair chain 0->1->2->3->4->5 or 0->6. A restore row, a Custom chain, a
requested override or a skipped setter is refused, and so is any destination
100 row without the feature or that capability. An unsettled row is handed
back as `pending` family `hyperBoost` (1-4) or `abandonedPrepared` (0). The
`hyperBoostRows` vectors pin both readers.

The legacy raw journal is imported unchanged as segment 1 at the first rollover.
Each immutable segment and active raw tail is bounded to 1 MiB. A segment ends
settled and a new operation starts with Prepared. Request chronology is checked
across segment boundaries; segmentation cannot restart the same request.
Original row bytes and all owner records remain intact indefinitely. No original
row is relabeled to the checkpoint publisher. Segmentation resets no owner or
attempt budget and frees no native transaction ID.

Files under owner/thermal-history use checked unsigned 64-bit counters:
segment-NNNNNNNNNNNNNNNNNNNN.bin contains exact raw rows. The corresponding
checkpoint file has LF-separated fields, ending in LF:

    OpenBlade.ThermalCheckpoint.v1
    sequence
    previous checkpoint SHA-256 (64 zeroes for sequence 1)
    exact raw segment SHA-256
    raw byte length
    row count
    service-thermal-mutation-v1/rows-v1

The roll file has header OpenBlade.ThermalRoll.v1, old physical active SHA-256,
new empty active SHA-256, checkpoint SHA-256, and the same producer/schema contract.
Both records are at most 2,048 bytes. Counter spellings are canonical decimal;
hashes are lowercase hexadecimal. Checkpoints form a checked contiguous chain.
Each read streams one bounded segment at a time and checks the complete namespace;
missing, extra, unknown, out-of-order or corrupt files remain fenced.

After rollover, thermal-mutation.journal contains header OpenBlade.ThermalActive.v1,
sequence, checkpoint SHA-256, contract, a blank line, then the bounded raw tail.
Before Prepared, Storage serializes the exact operation row and reserves the
complete progress rows of its destination's longest legal chain: six for a
preset pair, ten for Custom, and ten for a battery preset when the HyperBoost
exit is compiled, whose direct pair then leaves four unspent. An oversized
operation or unavailable reservation fails before Prepared and before firmware intent.

### Temporary-charge source cancellation IDs

A temporary-full-charge enable row (`tfc`) reserves eight `cancel_ids` for
its automatic source cancel, and an automatic cancel row (`origin` set) names
them as its `ids`. A cancel whose earlier attempt sent some of them draws
fresh IDs instead, and its row names the reserved ones in the optional member
`superseded_ids`, placed after `cancel_ids`:

- `superseded_ids` is valid only on an automatic cancel (`enabled` false,
  `origin` set). It is eight IDs, disjoint from the row's `ids`, and
  `cancel_ids` stays null. An explicit null is refused.
- The journal binds every automatic cancel to its live enable by
  `cancel_ids == superseded_ids`, or `cancel_ids == ids` when the member is
  absent.
- The first superseding request replaces the reserved IDs for that enable.
  The journal refuses any other automatic cancel request of the same enable
  after it, with reserved or fresh IDs, until the next temporary-charge 72
  or 73 row. So one enable's cancels send at most two ID sets.

Rows without the member keep their bytes, canonical form and intent digest,
and no existing row changes meaning. A reader that predates the member
refuses a row that carries it: the Rust domain type denies unknown fields, and
`WindowsNativeTemporaryChargeHistory` admits only its exact member set. That
refusal is permanent for the whole history. Managed recovery stops with
`NativeRecoveryException` (`FirmwareOwnerUnresolved`), and native
`Storage::open_with` fails. A row with the member stripped and rehashed is still
refused, because its `ids` no longer match the enable's `cancel_ids`. The
shared vectors in
`tools/openblade-windows-host/tests/data/temporary-charge-history-v1.json`
cover the valid sequences and each refusal for both readers, including a tfc
row with one unknown or duplicated member and an unchanged intent.

`service-thermal-mutation-v1/rows-v1`, the `tfc` contract
(`service-temporary-charge-v1/rz09-0581-v1`) and the `service-temporary-charge-v1`
capability keep their names only because no release has shipped a
temporary-charge reader yet: it first appeared in a882588c, which no tag or
`main` contains. The first release that ships the temporary-charge reader must
include the `superseded_ids` member in both readers. If it does not, adding the
member later needs a new `tfc` contract and capability.

Handback needs no new record field, since the record carries no transaction
IDs. A fresh retry is handed back by its progress and desired state like any
other cancel: a crash after its Prepared is an abandoned Prepared, after its
70 or 71 a pending temporary-charge write whose target reads the charge off,
and after its 72 nothing. The enable's charge stays live until the retry
confirms.

Before any job runs, a new Service process quarantines in its transaction
pool the journal IDs whose replies an earlier process may have left in the
mailbox (`transaction_seed`). Reading a reply does not clear it, and a new
process has answered nothing, so a confirmed request's IDs are as exposed as
an unsettled one's. The seed takes every ID that each family's newest fan,
battery and temporary-charge request may have sent, whatever its progress:
a fan record's `preflight`, `ids` and `cleanup`, a battery record's `ids`, and
a temporary-charge record's `ids` and `superseded_ids`. It adds the
reservations of the owners still live at the end of the journal (a manual fan
owner's `cleanup` and a temporary-charge owner's `cancel_ids`). It then takes,
newest first and as far as its limit allows, the IDs of known-not-submitted
(6) requests and `superseded_ids` sets. A retired fan request adds its
`preflight` and `ids`, not its `cleanup`: the newest fan row can be an older
owner's settle-only 51, which sends no 07/8C, so the retired request's
preflight can hold the newest 07/8C reply. Older settled requests, the
reservations of replaced owners and performance rows add none. Neither does
an earlier request's reply to a command that the newest request of its
family did not send again, such as a setter it never reached. With
keyboard Driver-mode ownership, the seed first takes the four IDs that
`keyboard-driver-mode.bin` names: the newest claiming 00/04 job's
baseline, setter, readback and spare, whatever the record's state, or,
after a revert, the IDs of the record that job's claim replaced. A
record that is absent, unreadable or refused adds none. At most 80 are
taken: the largest live set (78, the record's four included) always fits,
and 174 of the 254 IDs stay free. That is twice the most IDs reserved at
once (66: a live fan owner's whole record, a temporary-charge owner's
cancellation, the next fan request with its Custom levels, and a read with
its Custom levels). Mutation jobs run one at a time, so a Driver-mode job is
such a next request. Three such peaks would not fit beside any limit that
holds the live set. The journal format is unchanged.

The seed reads the journal as release does (`journal_read_release`). That
authenticates the active tail and every archived segment, opening the
historical package files that prove each battery and temporary-charge row's
producer, but never recovers. So an interrupted roll makes the seed find
nothing instead of completing the roll when the source is composed; the first
mutation's own read completes it, as before. The seed searches only the active
tail, so a request in an archived segment adds nothing. A failed read is
retried once. A read that fails fatal admission is not retried; the source
keeps that failure, as its own reads keep it. So an owner store inconsistent
with the journal, which `join_row`'s original owner binding rejects, stops
the Service at start, including the provisional start of a new package: the
startup status probe fails with the kept failure and the Service never
reports RUNNING. Before the seed, such a Service started and failed at its
first capture. A journal that still cannot be read or validated adds none,
which is the window before this seed, and an absent one does not delay
startup.

## Publication and metadata recovery

Under the same original custody, create-new segment, checkpoint and roll records
are flushed and independently reopened/read back. The exact old active bytes are
compared before publication. A separate thermal-mutation.journal.rollover file
stages the new active envelope; it is flushed/read back before same-directory
rename, final flush and independent readback. This staging name is never a
firmware submission pending record.

A complete matching roll/checkpoint/segment with independently settled old tail
may finish exactly that metadata publication. Full predecessor evidence is checked
before recovery. Partial or mismatched metadata remains preserved and denied.
An archive/checkpoint without a complete roll intent is deliberately denied.
After active rename, the same immutable chain authenticates the published state.
thermal-mutation.journal.pending always preserves possible firmware-submission
ambiguity; rollover never consumes, deletes or reclassifies it. No firmware work,
Retired record, replay or alternate command sequence is performed by recovery.

## Positive managed admission and proof compatibility

AcquireRetiredOwner parses the complete owner chain and thermal namespace under
the owner writer. Each raw row joins exactly one original Active owner record.
Every production proof route then checks the original signed package/generation,
startup policy, process launch and independent transport drain. Original custody
is acquired before the owner writer, including historical thermal generations
used by direct retirement. Current owner identity cannot replace those joins.

The stable generation commitment starts with SHA-256 of canonical lines headed
OpenBlade.ThermalGeneration.v1 and installation, epoch and service artifact ID.
For each original row in order, SHA-256 of OpenBlade.ThermalGenerationRow.v1,
previous generation digest, exact row SHA-256 (including LF), and original Active
owner-record SHA-256 extends it. Physical boundaries do not affect this digest.

OpenBlade.ThermalHistory.v1 commits sequence, checkpoint head and exact physical
active digest (or explicit known-empty). OpenBlade.NativeOwnerHistory.v2 commits
the legacy owner-chain commitment and fresh thermal commitment. Truly no-thermal
history keeps its existing v1 owner commitment.

Thermal producers require NativeRetirementProof.v3: the existing seven v2 fields
followed by the stable generation thermal digest. Global owner/thermal admission
is always fresh; retained-generation comparisons separately include the stable
thermal field. PublishProofSnapshot, ParseResolution, RequirePreservedResolution
and all RequireDrained consumers understand this version together. Existing v1/v2
snapshots remain preserved; v1/v2 proofs cannot certify thermal settlement.
A preserved v2 terminal can survive a fresh v3 read only when the independently
authenticated original generation has the explicit empty thermal digest. Adding
any original-generation thermal rows removes that compatibility; no old proof
can substitute for their settlement commitment.
Existing v2 opaque resolution/generation envelopes retain their explicitly parsed
inner proof version and field count.

A rollback reader must implement this grammar and v3 proof contract before a
mutation-capable producer is activated. Compatible rollback uses a fresh epoch,
including an identical package. Previous readers explicitly refuse thermal
history/new namespace, and current retirement refuses a legacy producer claiming
thermal rows. No evidence is deleted to make an incompatible predecessor start.
This stage performs no producer activation, packaging default switch or install.

## Native-to-managed handback

A thermal producer that crashed leaves its terminal owner record Active or
Retiring. Setup's RetireRecoverableSelection, under the exact reacquired custody
of that epoch, proves that process exited: its launch record exists and no drain
does. The proof is held in memory as the expected
attempts/exited-PID-CREATED.bin, with the lines OpenBlade.NativeAttemptExited.v1,
epoch, launch-record SHA-256, PID and creation time. Attempt history, thermal
classification, the handback record and the retirement proof are all validated
with that in-memory exit. Nothing is written until every refusal check passes;
a refused retirement leaves the attempts directory byte-identical. The commit
sequence is then: handback record, proof snapshot, exit marker, retirement. The
native launcher refuses the unknown marker name, so the marker is published only
directly before retirement. A crash anywhere in the sequence reruns from the
same evidence. A handback record already published is reused when it carries
the same epoch and history commitment; any other existing record refuses. The
proof snapshot and exit marker already published must be byte-identical and are
reused. Other readers accept only an existing exact marker. Metadata-only
producers never publish one, and only the custody holder of that epoch receives
the exit.

The handback is one-way. Native never settles the handed-back tail, and every
reader without a proven exit requires settled history, so no later native
generation can own firmware state after a handback; managed holds those
obligations from then on. Two cases still refuse retirement and preserve
history: a crash between an owner's Reserved and Active records, and a launch by
a process other than the terminal owner that left no drain.

With that exit, the tail is classified instead of refused. Every unsettled or
owned row must belong to the exited owner:

- pending: an open frontier past its last unsent step, a possibly submitted write;
  the HyperBoost exit's restore rows (80-83) are pending only for Battery Saver
  or Battery Balanced;
- abandonedPrepared: an open frontier at Prepared (0) or fan release
  preparation (31), neither of which has sent a setter;
- ownedFan: manual fan control the exited owner held (progress 30);
- liveTemporaryCharge: a temporary full charge the exited owner itself started
  (rows 72 and 73). One started by an earlier owner is not handed back.

A staged thermal-mutation.journal.pending is preserved and its SHA-256 committed.
Any .rollover file still refuses retirement.

The record lives in the handed-back generation's own directory of the native
namespace, ProgramData\OpenBlade.NativeEpochs\EPOCH\handback.json, beside the
history it is bound to, and shares that history's lifetime. Setup writes it
through the same epoch-files view that holds custody, with that view's
exact-path, non-reparse, trusted-ACL checks. Because it names user SIDs,
requested settings and override digests, the record and its resolution are
published with the private-custody recipe: a protected DACL granting only SYSTEM
and Administrators, owned by Administrators, rather than the namespace's
inherited read access for Users. Ordinary reads accept that descriptor.
Uninstall and purge removes ProgramData\OpenBlade but not the native namespace,
so a start after purge still finds the record and its resolution together with
the history that binds them. The record holds schema 1, the
history commitment, the record commitment, the items with their original request,
user SID and target, the staged-journal digest, and the SHA-256 of each user's
preserved manual-overrides.json.pending. The parser admits only the slot, family
and progress that classification produces, and refuses a temporary charge with
protection off. An existing record must carry the same history commitment; a
resolution without its record, or a record path that cannot be published, refuses
retirement.

The history commitment is the SHA-256 of canonical lines headed
OpenBlade.NativeHandback.v1. NativeRetirementProof.v4 appends it to the eight v3
fields, only for the generation whose record is non-empty; other generations keep
byte-identical v3 proofs. Retained and fresh proofs compare every thermal field
and must have the same version. The only cross-version match is the existing v2
to v3 empty-thermal case. The record commitment additionally covers the
pending-override digests (OpenBlade.NativeHandbackRecord.v1). Custody recomputes
the history commitment at every start, so a change to classification, to these
lines or to the record encoding would block every handed-back machine. The
known-answer vector tests/Fixtures/Parity/native-handback-record-v1.json pins,
for a classified journal and for a record using the other slots and a staged
journal, both sets of commitment lines, their digests and the exact record
bytes; change that format only with a new versioned header.

At every start, the Service's mandatory native custody gate reacquires the
retired owner and recomputes the handback from native history. Custody refuses
a handback record or resolution beside any generation it does not hand back,
and startup admission carries the generations it does hand back, with their
fresh history commitments, to the Service through ServiceMailboxHandoff, which
the hosted ServiceMailboxCustodyStartup opens before Worker. The store reads that
binding lazily; its first read is unexpected-exit startup in Worker, after every
hosted-service constructor. The
Service adopts a record only for a generation in that binding and only when its
history commitment matches. A bound generation without a record, a record that
differs from its history, or unresolved items from more than one generation keep
performance, fan and battery writes blocked. An admission that never opened, or
a custody refusal, is not read as "no handback": it propagates as the custody
failure it is. An empty binding reads nothing. Records are never deleted. The
Service does not check the v4 proof itself: the custody gate already recomputes
the same commitment from the same authenticated history, so the proof adds no
independent evidence.

Unresolved items start the unexpected-exit recovery path. Pending performance,
fan and battery writes, and a live temporary charge, become pending write
confirmations. They block Reapply and convergence until a fresh read resolves
them. A target comes from the record, not from user configuration, so these
confirmations resolve while recovery still waits for the active user; deferred
recovery can complete only once nothing is pending. A match is confirmed, and a
difference is reported as an external change for an explicit reapply. An
unreadable value stays pending. Thermal channels that still disagree after the
read's own retry resolve as a difference only when the next fresh observation
reports the same disagreement, which rules out a transition still in progress;
any other result restarts that count. The report then states that thermal
changes cannot be applied until both channels report the same state, because
every thermal write is refused while they disagree. Each outcome is persisted
to EPOCH\handback.resolved, beside the record and bound to its record
commitment, before the Service acts on it; if it cannot be persisted, the item
stays pending.

The external-change latch a mismatch raises lives in memory, as it does for
any managed external change; only the outcome is persisted. After a restart a
resolved item is not seeded again, and the Service starts from the user's
settings like any other start, without writing on native's behalf.

A confirmed temporary charge is persisted as "continuing" until a user's
configuration is active. It then continues as managed's temporary full charge
only for the user who started it, when that user's battery settings include it,
and becomes "matched"; otherwise it is "reported". A restart before that
sign-in seeds a continuing charge again, and a fresh read confirms it before it
continues, because it may have ended in between; this holds whether the charge
was confirmed from the live slot or from a pending enable request. One that has
ended is reported
without an external change, and no case writes the battery. Owned fans and
unsent preparations are reported only. Forced recovery from a handback alone,
without a managed run marker, arms no manual-fan safety floor, curve recovery or
shutdown handback, so managed makes no fan write at start, on later ticks or at
shutdown until the user reapplies a fan setting.

Still deferred: the Rust reader does not accept the exit marker, inherited fan
ownership is not adopted, and an interrupted rollover is not handed back.
Performance confirmation compares the preset only, not native GPU offsets.

## Keyboard Driver-mode record in Setup

The native Service journals its keyboard 00/04 setters in
owner\keyboard-driver-mode.bin, beside the thermal journal, and stages each
replace as keyboard-driver-mode.bin.pending. WindowsNativeDriverModeHistory
reads both names under the owner writer, and the owner-chain walk skips them as
it skips the thermal namespace. NativeDriverModeRecord parses the record with
the grammar of openblade-domain driver_mode_history.rs and replays its shared
vectors, tools/openblade-windows-host/tests/data/driver-mode-record-v1.json:
the same accepted records and fields, the same rejections, and the canonical
re-encoding. Setup never writes, renames or removes either name.

AcquireRetiredOwner classifies the record before thermal history, so every owner
admission applies the same rule: retirement, a crashed generation's handback,
managed mailbox custody and Begin.

- Absent: unchanged. Commitments, proofs and handback records keep their bytes.
- Clear or Owned: admitted. The record's SHA-256 joins the owner commitment
  under OpenBlade.NativeOwnerDriverMode.v1, so the retirement proof attests it.
  The handback record is the one the thermal history alone gives. Owned is
  reported only, as a setup-log warning at retirement: managed reads the
  keyboard mode before any Driver-mode write of its own.
- AcquireIntent or ReleaseIntent: refused. The setter may have reached firmware,
  and the handback has no item for it, so even a proven exit does not retire the
  generation. The refusal names the intent.
- Unknown: a record that does not parse, is a directory or reparse point,
  exceeds 2048 bytes, or names an owner the chain never made Active. Refused.
- A leftover stage, file or directory: refused, whatever the record holds. It
  may belong to a replace that never finished.

Each refusal is FirmwareOwnerUnresolved, names the case and leaves every owner,
attempt and generation byte as it was.

No admitted build can open owner custody over either name: a Service built
without service-keyboard-driver-mode-v1 refuses the record names in its owner
history, and WindowsNativePackage refuses that feature for packaging. An install
is therefore refused before anything is stopped (OpenForSetupAsync with an
incoming package), and again under the epoch writer in Begin, after owner
admission has named any intent or unknown record. The install gate checks
presence only, so it never holds the record open while a native Service may be
replacing it. Any record or stage blocks every later native install, a settled
Clear record included, and nothing in the product removes it. Only uninstall
opens the selection without an incoming package: re-running Install passes the
incoming package, and the product registers no Repair. On a development
machine the remedy is by hand: uninstall, confirm that the keyboard reads
Normal, and, with no native Service running, move owner\keyboard-driver-mode.bin
and any .pending stage out of %ProgramData%\OpenBlade.NativeEpochs as an
administrator before the next native install. This applies to a settled
record only: an intent, an unknown record or a stage also refuses the
uninstall's retirement and stays until it is resolved, as below.

Still deferred (stage 6a-5 D2 option a): a versioned handback item for the
record, pending for an intent and reported for Owned, with a managed 00/84 read
before its first Driver-mode write. Until then an intent fences retirement,
handback and managed mailbox custody until a native generation with keyboard
Driver-mode ownership resolves it read-only, and that build cannot be packaged.

## Verification limits

Executed checks use only inspected pure/injected test bodies, inert signed-package
fixtures and supplied own-SID private NTFS roots. Production adapters compile but
real identity, service, pipe, HID, sensor and helper suppliers never execute.
Injected publication failures are process-visible cuts, not physical power-loss
proof. The format retains lifetime disk evidence and linear streaming validation;
it does not claim constant disk use, adversarial privileged rollback resistance,
finite transaction-ID reuse, full Windows parity or runtime/cutover readiness.
