---
title: Keep native SCM lifecycle smoke on hosted Windows
author: Jack
date: 2026-11-09
branch: feat/rust-windows-readonly-host
touches:
  - ".github/workflows/rust.yml"
  - docs/windows-rust-development-service.md
  - tools/openblade-windows-host/Test-HostedScm.ps1
---

## Decided
Keep the development SCM test in the same-repository GitHub-hosted Windows PR step. Run the fixture host as LocalService from a unique, hash-checked staging path. Preflight the fixed development registration and pipe, verify startup contention and two native STOP cycles, then delete only the registration created by the test.

## Tried and ruled out
The injected SCM reporter proves host-side order but does not deliver a Windows SCM callback. Do not use the user workstation or broaden workflow privileges to fill that gap.

## Watch out
The runner can observe pipe availability without opening it. The developer pipe rejects the different-account runner, so foreground tests still own admitted-client and in-flight coverage.
