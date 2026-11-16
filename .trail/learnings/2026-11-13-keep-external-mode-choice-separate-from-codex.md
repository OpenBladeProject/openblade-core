---
title: Keep external mode choice separate from adapter sufficiency
author: Codex
date: 2026-11-13
branch: feat/all-external-power-modes
touches:
  - src/OpenBlade.Contracts/PerformancePowerSourcePolicy.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.cs
  - src/OpenBlade.Service/ServiceControlRuntime.cs
  - rust/crates/openblade-domain/src/automation.rs
---

## Decided
Offer every exact-model-capability-supported performance preset, Custom and HyperBoost on both confirmed external source classes. The user explicitly replaced the prior Balanced-only policy for limited adapters; no connector or watt threshold remains. Retain truthful limited classification, existing typed command contracts, EC/native power management, per-submission source and controller admission, independent readbacks, durable ownership and stop-on-ambiguity. Battery behavior and unknown firmware observations remain separate. Keep external-power discharge notifications unchanged. The Rust override eligibility change intentionally expands external cases from the frozen v1 matrix; preserve the historical fixture and test the explicit delta.

## Tried and ruled out
An above-180W or barrel-only exemption cannot be derived from wattage alone and is narrower than the requested product policy. Do not promote limited sources to PluggedIn, spoof adapters or write raw EC power/protection registers. Do not treat immediate MCU cached GET equality as proof of physical EC completion. Existing physical validation plans remain historical records.

## Watch out
ExperimentalBladeFactory receives raw Windows ExternalUnknown for AC presence, not reconciled adapter classification. Accept that only as preliminary external presence, then require a fresh known external firmware adapter before every thermal SET and lock the original Windows source through native submission. Preserve destination profiles across external source changes; retain battery/suspend cleanup. Custom includes saved levels and supported GPU intent; HyperBoost recovery must preserve the correct source/generation and restoration target. CPU voltage tuning remains a separate feature. Limited-source physical behavior has not been tested by this implementation run.
