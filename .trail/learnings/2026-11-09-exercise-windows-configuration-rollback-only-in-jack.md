---
title: Exercise Windows configuration rollback only in isolated test roots
author: Jack
date: 2026-11-09
branch: feat/rust-config-writer
touches:
  - tools/openblade-windows-host/src/configuration_writer.rs
  - docs/windows-configuration-recovery.md
---

## Decided
A Windows persistence replay can use the managed schema 1 recovery sidecar and the exact current profile, preference, and marker bytes. Create and flush the sidecar before any primary replacement, then keep it open until independent readback confirms commit or exact in-process restoration. An injected interruption after the final replacement still requires restart refusal.

## Tried and ruled out
Restart cannot infer authority from matching primary hashes or reconstruct a rollback from sidecar paths. The cross-language test exchanges UTF-8 documents through standard I/O so each runner can keep its own temporary root.

## Watch out
A freshly allocated test-only root, checked path components, and a cooperative writer lock do not admit production configuration. Trusted root and ACL checks, noncooperating namespace races, value validation, migrations, journal wiring, and power-loss durability remain separate work.
