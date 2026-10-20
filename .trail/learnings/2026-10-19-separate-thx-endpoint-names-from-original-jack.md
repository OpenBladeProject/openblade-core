---
title: separate THX endpoint names from original FX binding and runtimes
author: Jack
date: 2026-10-19
branch: feat/thx-driver-provisioning
touches:
  - docs/thx-installation-investigation.md
---

## Decided

Keep legacy THX fresh provisioning blocked on an original exact-model signed render FX binding and installation contract. The recovered THX 2.6.5 child extension already supplies topology list additions, child enumeration and acoustic profile selection; those do not establish the complete effect CLSID chain. Realtek 6.0.9909.1 base adapter slots +0x38/+0x40 select endpoint names, while a codec-family override and runtime endpoint flags remain unproved.

## Tried and ruled out

A bounded static trace linked the base adapter constructor/vtable to its wave/topology selectors. It did not derive THX CLSIDs from tuning. The known public factory mirror still lacks a matching newer Realtek integration package. Static normal/delay import audits of the pinned native client, rznb11/cached rznb12 payloads, configuration updaters and legacy service/APO/SCU found only Windows system library imports and no delay imports. Legacy VSSrvInit targets .NET Framework 4 Client Profile; modern MSI custom actions target 4.8, both covered by the existing Windows 11 24H2 baseline's 4.8.1.

## Watch out

Selectable endpoint names are not proof of each model's runtime selection. InterfaceSetting list additions and EP configuration properties are not complete FX CLSID binding. Final endpoint FxProperties alone cannot prove provisioning: preserve original signed packages, final lists/subtrees, adapter identity and setup order. Static imports do not exclude dynamic loading or prove a clean installation with Synapse absent. Do not execute vendor binaries, edit signed INFs or fabricate nodes/registry values to close a source gap.
