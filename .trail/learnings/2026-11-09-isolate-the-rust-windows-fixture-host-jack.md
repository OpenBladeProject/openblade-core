---
title: Isolate the Rust Windows fixture host from product provenance
author: Jack
date: 2026-11-09
branch: feat/rust-windows-readonly-host
touches:
  - ".editorconfig"
  - ".gitattributes"
  - ".github/workflows/rust.yml"
  - docs/windows-rust-development-service.md
  - tests/OpenBlade.Tray.Tests/RustReadOnlyHostTests.cs
  - tools/openblade-windows-host/.gitignore
  - tools/openblade-windows-host/Cargo.lock
  - tools/openblade-windows-host/Cargo.toml
  - tools/openblade-windows-host/src/main.rs
  - tools/openblade-windows-host/src/windows.rs
---

## Decided
Keep the fixture-only Rust Windows host in a standalone tools workspace. Test the actual Tray.Core ServiceClient by supplying its existing pipe-name constructor a random development pipe name. Native first-instance and anonymous-QoS tests complement the fake SCM lifecycle and managed codec replay.

## Tried and ruled out
A separate Cargo workspace nested under rust/ still enters product development native provenance, because Build-NativeDevelopment.ps1 hashes every file below rust/ outside target/. Use tools/openblade-windows-host to leave the product workspace and input set untouched.

## Watch out
Generic-write access used by the current C# duplex pipe also grants FILE_CREATE_PIPE_INSTANCE to the admitted SID. The development host confines its trust boundary to the same SID, logon LUID and Windows session. Do not carry that DACL over as a product endpoint policy. A real SCM invocation still needs separately authorized VM validation.
