---
title: Native handback deferred-recovery deadlock and exit marker ordering
author: Jack
date: 2026-11-19
branch: feat/native-managed-journal-handback
---

## Decided
- Native handback confirmations resolve while unexpected-exit recovery is still deferred for the active user. Their target comes from the Setup record, not user configuration, and their match reads no management state. Deferred recovery completes only when WritesBlocked is false, and a pending handback confirmation keeps WritesBlocked true. Skipping handback confirmations while deferred therefore deadlocked: neither could ever finish.
- The native exit marker (attempts/exited-PID-CREATED.bin) is held in memory as a PendingExit during admission. Attempt history, thermal classification, the handback record and the retirement proof are all validated with it. It is written only in the final commit sequence: handback record, proof snapshot, exit marker, retirement. The native launcher refuses an unknown marker name, so a marker written before a refusal would block native relaunch of a generation that was never retired.
- A handback alone forces unexpected-exit recovery but not managed fan ownership. Runtime Initialize takes recoveringManagedRun separately. Only a managed run marker arms manual-fan safety ownership, curve recovery and the shutdown fan handback.
- A mid-pair thermal crash resolves as a Mismatch when the read reports a ThermalChannelAgreement failure after its own retry. That needs an explicit reapply, which is the same as managed's own uncertain-write handling. Any other unreadable value stays pending.

## Tried and ruled out
- Gating handback confirmation on the deferral flag, so the user is known first. It deadlocks, as described above.
- Publishing the exit marker inside AcquireRetiredOwner. A later refusal, such as a rollover or a missing destination, leaves the marker behind and the attempts directory changed.
- Arming fan recovery from the forced recoveringUnexpectedExit flag. Managed then writes a fan safety floor over a manual fan that native, not managed, owned.

## Watch out
- After deferred completion, the base marker path latches Fans as an external change whenever fans are Automatic. This also happens with no handback at all. It is pre-existing and not a handback effect.
- The Service does not verify the v4 retirement proof. The mandatory custody gate recomputes the same history commitment from the same authenticated history, and the store checks the record's own commitment.
- The Seed slot check for unknown key and family pairs cannot be reached through the store, because the record parser already admits only the slots that classification produces. It is defence in depth, so a mutant there survives.
