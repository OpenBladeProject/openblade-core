---
title: Provision THX stacks without Synapse using exact package evidence
author: Jack
date: 2026-10-19
branch: feat/thx-driver-provisioning
touches:
  - src/OpenBlade.SessionAgent/ThxAudioProvisioningCommand.cs
  - src/OpenBlade.SessionAgent/ThxAudioProvisioningJournal.cs
  - src/OpenBlade.SessionAgent/WindowsThxAudioProvisioningPlatform.cs
  - src/OpenBlade.Tray/ThxAudioInstallLauncher.cs
  - docs/thx-audio.md
---

## Decided
Standalone THX setup uses the complete pinned rznb11 base installer and two immutable configuration updaters from the exact 709/710 Razer manifests, only on RZ09-0528 and RZ09-0530 with their reviewed USB and codec identities. A matching Realtek base driver remains a prerequisite. The catalog records full stack hashes and every supported-model applicability decision; core fixtures bind runtime and acquisition pins to it.

The setup action is explicit and separate from audio discovery/control. Acquire the per-user client first; if observational discovery is then complete, no elevation is needed. Otherwise launch a fixed installed Session Agent bridge with a clean environment. Elevated setup stages only verified official artifacts under a protected admin/System root, persists a flushed intent before each process, and independently verifies installed hashes, catalog trust/membership, active immutable configuration and service state.

## Tried and ruled out
Product 736 exposes the rznb12 client and configuration updaters but no base installer in the reviewed manifest. The RZ09-0581 support page and Microsoft Update Catalog exact-version search did not supply that missing source. Configuration updaters alone are not a stack; do not guess an installer URL or substitute rznb11 for rznb12. Pristine-only setup does not repair, upgrade or remove existing THX packages or presets.

## Watch out
A vendor exit code of zero does not prove installation; configuration updaters ignore their service stop/start results. Their CRT system() calls require a trusted ComSpec/PATH and protected temporary directory. Active-file hashing is bounded, and a reparse target must block even when ordinary intermediate file bytes are not yet pinned.

Unknown, canceled, timed-out, truncated or malformed submission records remain paused without replay. Only a recorded 3010 result plus a different Windows boot identity and fresh exact-stack checks may continue later unsubmitted steps. Never repeat the submitted step or reboot automatically. Simulated tests and disconnected screenshots do not establish physical installation success on either laptop.
