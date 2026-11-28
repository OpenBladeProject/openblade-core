---
title: Gate native fixture checks on supported Python
author: Jack
date: 2026-11-14
branch: feat/native-fixture-ci
touches:
  - ".github/workflows/build.yml"
  - build/NativeWindowsFixtures.ps1
  - build/native_windows_fixtures.py
  - build/test_native_windows_fixtures.py
  - tests/Build.Tests/CiBuildScope.Tests.ps1
  - tests/Build.Tests/NativeWindowsFixtures.Tests.ps1
---

## Decided
Require Python 3.11 or newer and hashlib.file_digest before fixture roots, own-SID setup or builds. Both PowerShell entry wrappers resolve and probe the first PATH interpreter, then use and record its actual executable, version and SHA-256. The Python driver independently checks the floor before argument-path resolution. Quality and build-contract jobs provision explicit Python 3.12 x64 through the repository's existing setup-python action convention.

## Tried and ruled out
The second cumulative review's ordinary-output-overwrite premise did not hold: existing global fixture properties already isolate .Fixtures.n outputs and assets, and copied native payloads remain with the corresponding managed output. Service's existing prerequisite restore closure includes actual Deployment; no additional missing conditional package was established. Those refuted premises do not justify another output-path or restore rewrite in this Python correction.

## Watch out
A runtime floor failure must be clear before source hashing can call a missing file_digest API or setup creates a root. Test missing and unsupported probes at actual scope entry without executing SID/setup/build work, plus a supported probe and its receipt. Hosted cold capacity and total workflow duration remain unmeasured; keep prior evidence seals immutable and review the incremental Python patch with the existing fixture-property context.
