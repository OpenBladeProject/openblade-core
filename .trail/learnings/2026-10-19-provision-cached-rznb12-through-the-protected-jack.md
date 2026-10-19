---
title: provision cached rznb12 through the protected signed inner installer
author: Jack
date: 2026-10-19
branch: feat/thx-driver-provisioning
touches:
  - AGENTS.md
  - CHANGELOG.md
  - docs/thx-audio.md
  - docs/thx-installation-investigation.md
  - docs/thx-legacy-interface.md
  - src/OpenBlade.SessionAgent/ThxAudioAdmission.cs
  - src/OpenBlade.SessionAgent/ThxAudioCompatibility.cs
  - src/OpenBlade.SessionAgent/ThxAudioHelperCommand.cs
  - src/OpenBlade.SessionAgent/ThxAudioProvisioningJournal.cs
  - src/OpenBlade.SessionAgent/ThxAudioProvisioningPackage.cs
---

## Decided

RZ09-0581 fresh THX setup may use the original signed cached rznb12 4.2.37.1621 installer as a separate catalog release asset. The owner confirmed existing written Razer permission covers THX installers and this delivery method. Retain the original license notice, carrier hash/size/THX signer and exact asset URL. Synapse must not be installed for eventual fresh-install acceptance; closing Synapse proves only standalone control of an existing stack.

Keep cached 1621 and installed 2499 as separate complete pinned stacks. Do not mix package members or replace a complete admitted stack. Require the pinned 1846 active properties/surround configuration and native client. For cached 1621, read and validate both endpoints and their full preset/property contracts before mutation, tied to fresh stack identity. Physical installation/runtime/audible validation of 1621 is still pending.

## Tried and ruled out

Current and advertised archived product736 manifests have no base-installer URL. The exact original installer filename inferred under Razer's common THX directory returned403; this does not prove existence or absence. The full Windows cache carrier contains the complete signed Burn/MSI/CAB payload; a detached cached Burn engine does not.

## Watch out

Both rznb11 and rznb12 outer managed wrappers extract elevated Burn into user-writable Temp/LocalAppData. Never execute them. Lock and verify the outer as data, boundedly extract the exact raw-DEFLATE range into protected nonce staging, independently verify the complete inner hash/size/signature, and execute only the fixed inner /quiet /norestart with a protected /log. Bind durable recovery to the whole recipe, arguments, extraction, stack and configuration pins. Installer exit0 can conceal SetupAPI failure; independently check catalogs, files, configuration and exact running service. No vendor code or installed stack was changed during this investigation.
