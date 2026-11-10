---
title: Compile the installed service without fixture modules
author: Jack
date: 2026-11-10
branch: feat/rust-installed-service-boundary
touches:
  - tools/openblade-windows-host/src/installed_main.rs
  - tools/openblade-windows-host/src/scm_host.rs
---

## Decided
The installed binary has a standalone crate root. Only the bounded admission code and shared SCM primitives compile into it. Test the candidate through its own Rust harness.

## Tried and ruled out
A string comparison in the original entry did not exclude fixture modules from the candidate compilation graph.

## Watch out
Trail matched wording from unrelated device and deployment learnings. The status protocol in the first learning applies to the future real endpoint. Its warning is about synthetic fixture listeners. The branch exercises neither native registration nor any hardware or installed endpoint.
