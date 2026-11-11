---
title: Authenticate native metadata recovery with role capabilities and retained owner writer
author: Jack
date: 2026-11-11
branch: feat/native-metadata-recovery
touches:
  - src/OpenBlade.Deployment/WindowsNativePackage.cs src/OpenBlade.Deployment/WindowsNativeInstalledSelection.cs tools/openblade-windows-host/Cargo.toml
---

## Decided
Authenticate new native artifact/record v2 role capabilities transitively through the original signed package and exact PE/compiled identity. Preserve v1 schema and byte-exact records. Full Cargo features are provenance for both Service and combined Agent; the Agent owns capabilities none. Metadata-only Service composition excludes real firmware and HID acquisition at compilation. Hardware constructor coverage is a separately selected composition without metadata capability.

Retirement derives authority from the OLD authenticated artifact. Only v2 metadata artifacts may classify valid unmatched launches as abandoned-metadata. V1 cleanup accepts original private custody or its authenticated older trusted-owner/no-unprivileged-write descriptor under the same exclusive lock, with every launch drained; no ACL repair or retroactive metadata privilege. Retain epoch writer -> service custody -> owner/writer.lock through fresh complete owner/history validation and all proof/retirement publication. Existing native ownership takes custody before owner and never takes epoch writer.

## Tried and ruled out
A current denial or unused marker does not prove no-firmware composition. Hiding Service Cargo features from a combined Agent loses provenance. Incoming capabilities cannot authenticate old history. A snapshot followed by unlocked retirement leaves a firmware reservation race. Missing owner/writer.lock is unknown, not known empty. Raw process absence, stopped state, reboot and custody release certify no firmware drain.

## Watch out
The shared 4096-launch budget reserves drain space. Preserve immutable package mappings, launches/drains, content-addressed old/new proofs and partial staging. Public readers can still block legacy cleanup. Before every Rust test execution preserve the exact listed pure/injected allowlist; exclude private pipes, identity probes and role mains. Internal offline migration only; native firmware activation/default cutover and OS/physical validation remain denied.
