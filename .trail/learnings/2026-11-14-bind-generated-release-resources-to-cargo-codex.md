---
title: Bind generated release resources to the Cargo root crate
author: Codex
date: 2026-11-14
branch: feat/all-external-power-modes
touches:
  - build/NativePayload.ps1
  - tests/Build.Tests/NativePayload.Tests.ps1
---

A Rust source fingerprint does not cover the bytes behind a trailing linker-argument path. A tiny release probe rebuilt version.res at the same path from version1 to version2, then repeated identical cargo rustc arguments; Cargo reported fresh and returned the original PE version and binary hash. Hash the compiled resource and pass that hash as a root-only cfg argument after cargo rustc --. Record and validate the same flag/hash in native provenance, alongside the independent PE version, DLL hash and committed-source checks. The fixed same-path1/2/1 resource round trip returned the requested versions and restored the exact first binary hash. Keep the all-crate Rust source fingerprint and shared publication mutex; a release identity change needs only the root crate to relink.
