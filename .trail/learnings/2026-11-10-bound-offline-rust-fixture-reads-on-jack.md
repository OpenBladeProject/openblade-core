---
title: Bound offline Rust fixture reads on the open handle
author: Jack
date: 2026-11-10
branch: fix/rust-fixture-bounded-read
touches:
  - build/development-deployment/RustFixtureCandidate.ps1
  - tests/Build.Tests/Test-RustFixtureCandidate.ps1
---

## Decided
Fixture candidate creation reads each pinned role through one read-only handle. Capture its length once, reject out-of-range sizes before allocation, and require an exact read and EOF. Role hashes and PE validation still run before package creation.

## Tried and ruled out
Checking FileInfo.Length before a separate ReadAllBytes leaves the allocation governed by a later path lookup. The focused fixture test uses a scratch role sized one byte over 32 MiB and checks that rejection leaves no package.

## Watch out
Trail keyword matches to the private Rust launch-name and CPU closure-write learnings were false positives on this slice. The fixture script reads inputs and never launches them. The test grows only a disposable input and does not touch the CPU closure or its journal.
