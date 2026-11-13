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

The legacy raw journal is imported unchanged as segment 1 at the first rollover.
Each immutable segment and active raw tail is bounded to 1 MiB. A segment ends
settled and a new operation starts with Prepared. Request chronology is checked
across segment boundaries; segmentation cannot restart the same request.
Original row bytes and all owner records remain intact indefinitely. No original
row is relabeled to the checkpoint publisher. Owner and attempt budgets and
finite native transaction IDs are neither reset nor replenished.

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
Before Prepared, Storage serializes the exact operation row and reserves six
complete progress rows, covering the larger legal branch. An oversized operation
or unavailable reservation fails before Prepared and before firmware intent.

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

## Verification limits

Executed checks use only inspected pure/injected test bodies, inert signed-package
fixtures and supplied own-SID private NTFS roots. Production adapters compile but
real identity, service, pipe, HID, sensor and helper suppliers never execute.
Injected publication failures are process-visible cuts, not physical power-loss
proof. The format retains lifetime disk evidence and linear streaming validation;
it does not claim constant disk use, adversarial privileged rollback resistance,
finite transaction-ID reuse, full Windows parity or runtime/cutover readiness.
