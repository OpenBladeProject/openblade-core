---
title: Preserve unresolved configuration transactions on restart
author: Jack
date: 2026-11-09
branch: feat/rust-config-recovery
touches:
  - src/OpenBlade.Service/BatteryConfigurationStore.cs
  - tools/openblade-windows-host/src/configuration.rs
---

## Decided
Read the machine configuration transaction in two layers. The fixed recovery-required entry blocks restart even if the primary aggregate is coherent. Without it, verify the marker over the original UTF-8 streams before interpreting the payload. Full profile and preference validation is a separate migration gate.

## Tried and ruled out
The managed recovery record stores prior existence and artifact names but no preimage or intended hashes. The writer can verify backups during in-process rollback because it still has the original bytes. After restart those bytes are unavailable as independent evidence, so following recorded paths or clearing the record based on matching primary hashes is unsafe.

## Watch out
Probe the fixed sidecar before opening the primary files. Preserve uncertain storage errors separately from absence. An inspection result describes one coherent snapshot and is insufficient to admit a later write.
