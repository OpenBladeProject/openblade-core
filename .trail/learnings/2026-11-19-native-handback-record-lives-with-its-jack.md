---
title: Native handback record lives with its epoch history
author: Jack
date: 2026-11-19
branch: feat/native-managed-journal-handback
---

## Decided
- The native handback record and its resolution live in the handed-back generation's own epoch directory (ProgramData\OpenBlade.NativeEpochs\EPOCH\handback.json and handback.resolved), not under ProgramData\OpenBlade. They share the lifetime of the history that binds them; UninstallAndPurge deletes ProgramData\OpenBlade but keeps the native namespace, so a purge can no longer leave a bound generation without its record (which blocked Performance, Fans and Battery forever).
- Custody refuses a handback.json or handback.resolved beside any authenticated generation it does not hand back. That replaced the Service store's own directory scan (unknown entries, orphans, unbound records): the store reads only the bound epochs and an empty binding does no I/O.
- The binding travels as ManagedMailboxStartup.Admission.NativeHandbacks (commitments plus a view opener) through ServiceMailboxHandoff.NativeHandbacks into ServiceComposition.CreateNativeHandbackStore. The handoff throws InvalidOperationException while unopened, and the store calls binding() outside its try, so a custody refusal or unopened admission propagates instead of reading as "no handback". This keeps the planned I/O-free handoff (fix/native-branch-red-tests) a small merge.
- A temporary charge confirmed before its owner signs in is persisted as "continuing". A later start seeds it again as an obligation and a fresh read confirms it before inheritance, because it may have ended in between; nothing writes the battery.
- A handback thermal-channel disagreement resolves as mismatched only on the second consecutive fresh observation; the message says thermal changes cannot be applied until both channels agree, not "reapply".
- The mismatch latch is process-local by parity with managed external changes; only the outcome persists.

## Tried and ruled out
- Persisting the TFC owner and continuing without a fresh read after restart: the charge can end across the restart, and continuing desired state would then write the battery on native's behalf.
- Resolving the IServiceControlRuntime from AddServiceControlRuntime in a test to pin the nativeHandbackStore argument: it constructs production CPU, cooling-pad and guardian backends. The binding logic was moved into CreateNativeHandbackStore and tested there instead.

## Watch out
- Execution-unverified unelevated: the custody HandbackView production arm (WindowsNativeEpochFiles.OpenProtected(root)) and the nativeHandbackStore registration argument in ServiceComposition; tests use the private-fixture arm. Mutants of both survive by design.
- `sed -i` in Git Bash on this host can strip CR from a CRLF file; rebuild CRLF files with tr -d and sed 's/$/\r/' into a new file.
- Service test builds with -p:OpenBladeNativePackageTests=true go to bin\fixtures.Fixtures.n\...; --list-tests without that property reads a stale binary.
