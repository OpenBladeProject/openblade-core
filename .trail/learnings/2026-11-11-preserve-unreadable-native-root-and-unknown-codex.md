---
title: Preserve unreadable native root and unknown owner history
author: Codex
date: 2026-11-11
branch: feat/rust-native-origin-integration
touches:
  - src/OpenBlade.Deployment/WindowsNativeEpochFiles.cs
  - src/OpenBlade.Deployment/WindowsNativeInstalledSelection.cs
  - src/OpenBlade.Deployment/WindowsNativeTrustLifecycle.cs
  - tests/OpenBlade.Deployment.Tests/WindowsNativeEpochTests.cs
---

## Decided
Fixed native roots and historical stores treat only explicit FileNotFound/DirectoryNotFound as absent. Propagate access and unknown I/O failures, reject non-directory/reparse roots, and pin a present legacy private parent before accepting child absence. Keep the private broker/parent DACLs unchanged. Retirement/revocation enumerates all owner entries, so an unrecognized partial/orphan entry cannot masquerade as empty history.

## Tried and ruled out
Directory.Exists suppresses access and I/O errors into false. Using that result before public-root provisioning or private enrollment revocation could bypass unknown historical state. Filtering only owner-*.bin similarly hid unrecognized owner state. Neither absence of recognized rows nor metadata retirement certifies native worker drain.

## Watch out
These checks are source/injected/private-filesystem evidence. Do not probe installed roots or manufacture ownership in an offline test. Status still denies firmware admission without actual adapter/activity/managed-drain suppliers. Preserve unknown history for explicit recovery rather than relocating, adopting or deleting it.
