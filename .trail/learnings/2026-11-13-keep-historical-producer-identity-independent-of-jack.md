---
title: Keep historical producer identity independent of Service root
author: Jack
date: 2026-11-13
branch: feat/windows-rust-battery-policy
touches:
  - tools/openblade-windows-host/src/native_package.rs
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_history_store.rs
---

## Decided
Keep native_package shared across installed Service and Agent roots. Historical producer verification receives the original immutable installation, epoch and service-artifact identities from the Service boundary, validates their shapes, and pins the same protected original package proof.

## Tried and ruled out
Referring to crate::native_owner_record::Record in this shared helper compiled for the Service alone but failed the actual combined Service/Agent build because Agent has no owner-store module. Do not add Service ownership machinery to Agent merely to make the helper compile.

## Watch out
Compile materially different feature/entry compositions before source-stage closure. The battery-selected and fan-only Service builds do not cover the combined Agent root. Compile-only failures remain failures; preserve their original logs and rebuild clean-head markers after the bounded repair. This change grants no runtime or hardware authority.
