---
title: Setup reader for the native Driver-mode record (6a-5 S6)
author: Jack
date: 2026-11-24
branch: feat/native-driver-mode-setup-reader
---

## Decided
- `WindowsNativeDriverModeHistory` (Deployment) classifies `owner\keyboard-driver-mode.bin` and its `.pending` stage inside `AcquireRetiredOwner`, before thermal history, so retirement, crashed-generation handback, managed mailbox custody and Begin all apply one rule. D2 option (b): an AcquireIntent or ReleaseIntent, an Unknown record (unparseable, directory or reparse point, over 2048 bytes, or an owner the chain never made Active) and a leftover stage (file or directory) refuse with `FirmwareOwnerUnresolved` and keep every byte. Owned and Clear are admitted.
- The owner-chain walk skips both names, as it skips the thermal namespace. Without the skip the record was parsed as an owner record and refused everything.
- A settled record joins the owner commitment as `OpenBlade.NativeOwnerDriverMode.v1(commitment, sha256(record))`. Absent keeps the exact old commitment, so existing proofs and handback vectors keep their bytes. The handback record never sees the Driver-mode state: Owned is reported only, as a setup-log `Trace.TraceWarning` at retirement.
- Downgrade: no admitted native build can open owner custody over either name (no feature means `load_history` parses them as owner records; packaging refuses the feature). `RequireAbsentForIncoming` refuses an install by presence alone, in `OpenForSetupAsync` before anything stops and again in `Begin` after owner admission has named any intent. Presence only: Setup holding the record open with FILE_SHARE_READ would make a running native Service's rename-over fail and close its ledger.

## Tried and ruled out
- The 2048-byte bounds as killable gates: removing the reader's check or `Parse`'s survives, because a valid record is under 200 bytes and the grammar refuses anything longer. They bound memory, not admission. Removing the contract comparison survives too: the canonical re-encode writes the constant contract, as the Rust learning found for the header.
- Replaying `follows` from the shared vectors: Setup never writes a record, so only parsing is replayed. Every record a transition names must still parse canonically.

## Watch out
- `NativeCanonical.ParseLines(bytes, header, lines)` counts the header in `lines`. The record is header plus six fields: pass 7.
- `NativeRecoveryException` is an `IOException`, so `AcquireRetiredOwner`'s generic catch would rewrap a Driver-mode refusal into its generic message. A `catch (NativeRecoveryException)` rethrow precedes it.
- Hand mutants that delete a whole statement fail the build on IDE0055 (leftover indentation), which is not a kill. Replace the statement with an equivalent no-op instead.
- MSYS `sed -i` rewrites CRLF files as LF even when the pattern does not match. Run `unix2dos` on every touched file before committing.
- Option (a), the v2 handback item, must remove the intent refusal for the handed-back generation only, and must decide whether Owned joins the handback commitment. Until then an intent fences managed mailbox custody too.
