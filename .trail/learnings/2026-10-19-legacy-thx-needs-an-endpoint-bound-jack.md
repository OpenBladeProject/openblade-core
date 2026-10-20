---
title: Legacy THX needs an endpoint-bound COM backend
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

RZ09-0482, RZ09-0483 and RZ09-0484 use the older THX 2.6.5.0 service, not the V4 JSON client. The pinned service type library and official discovery helper establish IVSSrvSettings/IVSSrvTHXSettings, Init(GetCurrentProcessId()), output and listening-mode reads, and the spatial setter's originator plus bounded notification buffer. Offer source-backed experimental manual spatial control only after exact host, USB, codec, installed runtime and COM-registration admission. Match the service output against fresh Windows default roles, physical form factor and actual endpoint-devnode codec ancestry. Preserve independent post-write readback and stop on ambiguity.

Represent legacy EQ as observed-only and block presets and automation instead of borrowing the newer models' properties. Audio IPC v2 carries that distinction. Tests require every supported model to have an explicit THX or non-THX decision; RZ09-0328's exact specification identifies Dolby Atmos.

## Tried and ruled out

The fixed native float[31] EQ buffer does not establish 31 frequency bands or the modern toggle and preset semantics. Generic Microsoft Update Catalog THX components and exact Realtek codec packages do not establish the missing OEM child enumeration and acoustic tuning. Catalog their hashes and signatures as blocked component evidence, not a complete installation candidate. The rznb12 base installer is still unavailable; rznb11 must not substitute for it.

## Watch out

The helper pipe loop can resume on a different thread after awaits. Construct, use and dispose legacy COM synchronously per request. Verify the actual registered AVX2 or SSE2 System32 APO variant; the INF installs alternatives, not both. Use limited process-image queries for the LocalSystem service, not Process.MainModule. Discovery must not activate COM. Local-server activation can race a service exit and activate its registered service despite running checks, so do not promise zero activation. Factory COM path normalization and native/audio behavior remain physically unverified; reject unreviewed forms rather than weakening admission.
