---
title: Bind installed native role hashes to authenticated package bytes
author: Codex
date: 2026-11-10
branch: feat/windows-native-role-inventory
touches:
  - build/development-deployment/WindowsNativeRoleInventory.cs
  - build/development-deployment/WindowsNativeRoleInventory.md
  - tests/OpenBlade.Deployment.Tests/WindowsNativeRoleInventoryTests.cs
---

## Decided

Use an offline fixed Windows x64 native role inventory for the distinct installed Service and Session Agent Cargo outputs. Bind the resource to the authenticated outer package digest and independently selected revision/version before exposing either expected SHA-256. Only a product payload trust decision can admit the resource.

## Tried and ruled out

The LocalDev signed manifest covers the complete installer, and Setup's fresh source/copy inventory checks staging fidelity. Neither supplies these candidate role hashes at the pinned base. An authenticated scratch package or loose digest cannot provide product authority.

## Watch out

The future reader must authenticate and read the fixed resource from the same package snapshot, reject ambiguity, and keep scratch provenance distinct. The offline seam is not a Burn extractor, release-schema change, installer feature, or readiness result.
