---
title: Separate Rust GPU close from the signed scratch roles
author: Jack
date: 2026-11-10
branch: test/rust-gpu-close-integration
touches:
  - tools/openblade-windows-host/Cargo.toml
  - tools/openblade-windows-host/src/session_gpu.rs
  - tests/OpenBlade.Tray.Tests/RustSessionAgentTests.Gpu.cs
  - tests/OpenBlade.Tray.Tests/RustSessionAgentTests.Integrated.cs
  - tests/OpenBlade.Setup.Tests/SetupBackendCutoverTests.PrivateRust.cs
---

## Decided
Use the explicit gpu-close-fixture Cargo feature plus the gpu-foreground simulated-close argument for the inert decision fixture. Keep scripted inventory independently enabled. Build both signed scratch roles together with default features, then copy the separate opt-in Agent into a distinct directory. The signed role pins refer to 91b344c01f8346799cb4d7c764e758d19c8ad180. Managed v79 Fan Stop used the exact pinned service bytes.

## Tried and ruled out
An Agent runtime argument alone leaves simulated action code in the signed candidate. The default build rejects that argument and both actions on its query endpoint. The opt-in build still refuses both actions in available mode. Unified readiness checks the 15 percent grouped owner and omits the helper while exercising Display and foreground. The isolated signed scratch suite passed success, rollback and signature/role rejections. An earlier rollback run stopped at a broken service pipe before GPU readiness; the isolated rerun passed with identical inputs.

## Watch out
Scope Trail checks to the integration delta as well as recording the default output. Lexical overlaps in the inherited workflow and fixture vocabulary do not change the private pipeline. Keep the opt-in executable out of signed Setup inputs. Production signing, native registration and rollback, real inventory/retained process authority, and hardware handback require separate evidence.
