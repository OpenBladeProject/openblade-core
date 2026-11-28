---
title: Hold the verified LocalDev snapshot through native inventory reads
author: Codex
date: 2026-11-10
branch: feat/phase-9l-authenticated-package-reader
touches:
  - build/development-deployment/WindowsNativeRoleInventory.cs
  - build/development-deployment/WindowsNativeRoleInventory.md
  - tests/OpenBlade.Deployment.Tests/WindowsNativeRoleInventoryTests.cs
---

## Decided

For the offline standard LocalDev path, verify the installer size and SHA-256 from the signed manifest through a Windows read-share handle. Rewind that same stream for a bounded inventory adapter. Require the fixed inventory in the WixAttachedContainer payload tree owned by OpenBladeSetup. Carry the package digest, version, and revision in a snapshot distinct from product admission.

## Tried and ruled out

The cached pinned WiX 7.0.0 CLI is available and InstallerValidation.ps1 uses it to expand a build artifact by path. That validation checks expanded output against publish inputs. Same-handle access and preservation of duplicate physical members through expansion are unverified. The pinned product bundle has no authored role inventory.

## Watch out

The future adapter needs evidence for unique manifest ownership, physical member multiplicity, and bounded extraction. Independently retain protected enrollment and product admission before granting role-hash authority. The offline fixture and scratch signing policy establish only test byte identity.
