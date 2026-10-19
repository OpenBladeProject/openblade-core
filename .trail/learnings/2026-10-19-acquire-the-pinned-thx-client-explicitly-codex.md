---
title: Acquire the pinned THX client explicitly without changing audio state
author: Codex
date: 2026-10-19
branch: feat/thx-component-download
touches:
  - src/OpenBlade.Tray.Core/ThxNativeComponentPackage.cs
  - src/OpenBlade.Tray.Core/ThxNativeComponentTrust.cs
  - src/OpenBlade.Tray.Core/ThxAudioViewModel.cs
  - src/OpenBlade.SessionAgent/ThxAudioAdmission.cs
---

## Decided
Issue #808 now has an explicitly requested acquisition path: the THX page offers a download only for unavailable components when neither reviewed local client is valid. It downloads only the exact 1.1.1.0 client from the pinned Razer HTTPS host into current-user OpenBlade/Components/THX/1.1.1.0. Verify exact length, SHA-256, Windows Authenticode trust and the reviewed Razer signer before locked atomic publication. The catalog records metadata in a separate upstream-component schema and does not redistribute DLL bytes. User authorization explicitly changes the prior installed-client-only acquisition rule; it does not authorize drivers, services or audio writes.

## Tried and ruled out
Do not reopen the staging path with CreateFromSignedFile while retaining a write/DELETE-access handle: its sharing behavior conflicts. Extract the verified signer certificate from the successful WinVerifyTrust provider state instead. Retain the same locked handle through atomic publication. InvalidDataException derives from SystemException rather than IOException; resolver, admission and UI failure handling must catch it explicitly. Tests exposed both assumptions before integration.

## Watch out
The button performs file acquisition only and prompts an explicit Read settings action afterward; it must not connect, clear uncertain-write recovery or trigger audio mutations. Discovery/startup never download. The Session Agent prefers a verified OpenBlade component, retains verified Synapse fallback, and still verifies exact host, codec, driver packages, configuration, service and competing-controller state. Every native load retains the verified read lock. Signature validation is synchronous Windows trust work: cancellation prevents publication but must wait for WinVerifyTrust to return. Keep a coordinated full-install requirement because the resolver changes in Session Agent; IPC contracts remain unchanged. Disconnected previews inject download/component-presence delegates and never use network or production file discovery. Catalog metadata is provenance; runtime uses locally reviewed pins, not a live mutable catalog response.
