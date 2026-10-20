---
title: Legacy THX control does not broaden fresh provisioning
author: Jack
date: 2026-10-19
branch: feat/thx-driver-provisioning
touches:
  - AGENTS.md
  - CHANGELOG.md
  - docs/thx-audio.md
  - src/OpenBlade.Session/SessionAudioIpcCodec.cs
  - src/OpenBlade.Session/SessionAudioMessages.cs
  - src/OpenBlade.SessionAgent/SessionAudioRequestDispatcher.cs
  - src/OpenBlade.SessionAgent/ThxAudioAdmission.cs
  - src/OpenBlade.SessionAgent/ThxAudioCompatibility.cs
  - src/OpenBlade.SessionAgent/ThxAudioHelperCommand.cs
  - src/OpenBlade.SessionAgent/ThxAudioHelperProtocol.cs
---

## Decided

The legacy THX backend extends installed-component control, not the fresh-setup installer. RZ09-0482/0483/0484 use their own pinned 2.6.5 COM contract and exact output admission. Fresh setup remains pristine-only rznb11 on RZ09-0528/0530; rznb12 and legacy installation remain blocked by their documented missing packages. The product minor version is 0.38.0; the native client component remains 1.1.1.0.

## Tried and ruled out

Trail check's 20 matches on this branch are lexical overlaps with preserved exclusions and unrelated SnapTap, Fn and Blade 18 history. The change explicitly forbids generic component provisioning, guessed EQ semantics and cross-family installers. A past diagnostic's product version is provenance, not a rule freezing future product versions. No source-backed HID command or per-write guard is widened by the audio work.

## Watch out

Review actual admission and payload contracts rather than treating shared words such as native, generic, preset or driver as a semantic conflict. Keep the recorded installation blockers and pending exact-model physical validation visible. A source-reviewed experimental backend does not prove audible recovery on a reporter's machine.
