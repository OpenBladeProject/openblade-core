---
title: Bind Rust fixture roles in the Setup cutover rehearsal
author: Jack
date: 2026-11-10
branch: test/rust-setup-phase-7b
touches:
  - tests/OpenBlade.Setup.Tests/SetupBackendCutoverTests.cs
  - tests/OpenBlade.Setup.Tests/WindowsSetupPlatformTestFactory.cs
---

## Decided
Use pinned native Windows x64 fixture bytes for the service and Session Agent roles in the bundle-managed Setup rehearsal. Check the independent role digests and native PE headers in the real staging directory, then require typed service health and simulated Session Agent readiness before the test permits commit.

## Tried and ruled out
An inventory built from caller-selected input can show that copying preserved bytes, but cannot establish the role. Put the role pins in the test and inject an absent or substituted role after activation to exercise restoration of the prior tree.

## Watch out
The fixture binaries are build-only inputs. Setup's simulated sign-in boundary can fail inside the rollback window, but this does not establish runtime Session Agent readiness, signed payload trust, or installed behavior. The opt-in test pins identify one local toolchain/linker build; rebuilding elsewhere may produce different bytes.
