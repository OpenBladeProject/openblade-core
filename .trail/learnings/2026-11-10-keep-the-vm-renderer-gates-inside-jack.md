---
title: Keep the VM renderer gates inside the shared deadline
author: Jack
date: 2026-11-10
branch: test/rust-phase-5j-native-scm-fixture
touches:
  - tools/openblade-windows-host/src/windows/scm/vm.rs
  - tools/openblade-windows-host/vm-client/Program.cs
  - docs/windows-rust-phase5j-vm.md
---

## Decided
- Build the VM callback as its own required-feature executable. The ordinary developer host keeps its command surface even with all features enabled.
- Bind the VM launcher-to-STOP_PENDING and renderer-release windows below the existing renderer deadline. The VM records a closed pipe response only while SCM independently reports STOP_PENDING.
- Transfer the admitted data handle into the writer instead of reopening it. Only a real STOP authorizes a renderer acknowledgement.

## Tried and ruled out
- Starting an independent full-length STOP timer ignored that the accepted Off had already started the renderer deadline.
- A generic managed IOException could misclassify capacity rejection as pipe closure.

## Watch out
- An injected reporter and scratch NTFS tests prove offline ordering only. The clean-VM callback, LocalSystem peer and native cleanup remain live gates.
