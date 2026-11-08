---
title: Verify stage4 review fixes without changing the frozen ABI
author: Jack
date: 2026-11-08
branch: feat/rust-codec-abi
touches:
  - docs/rust-boundaries.md
  - rust/README.md
  - rust/Test-NativeInterop.ps1
  - rust/crates/openblade-ffi/include/openblade.h
  - rust/crates/openblade-ffi/src/lib.rs
  - rust/crates/openblade-ffi/src/tests.rs
---

## Decided
The mandatory Claude advisory review succeeded at base3fff72d872606ac38738ebce1d5058e8c355be6e and original head23c1b90bb7d8668afc33664c7cf65bead929ff5e with claude-opus-5-5/high. Validate its findings independently and attribute only that original snapshot. Fix the supported Windows-x64 developer-prompt failure by always importing the selected MSVC architecture, and explicitly reject targets outside the four reviewed native CI lanes. Harden unwind containment by forgetting caught panic payloads, preventing a panicking payload destructor from unwinding out of extern C. Private tests exercise both exports after computation with ordinary and destructor-panicking payloads while preserving output/error integrity.

## Tried and ruled out
Keep documented error-record-size-before-version precedence: unknown output records cannot be written and the host queries ob_abi_version before loading v1 operations. Exact ABI equality is an explicit stage contract, not major/minor negotiation. No evidence supports arbitrary additional NativeLibrary exceptions for the validated inputs, and fatal faults are outside containment. Defer unmeasured dormant-wrapper allocation tuning, duplicate-read/constant/lint cleanup and test deduplication; independently asserted frozen offset tables are useful conformance evidence. Guard mutation tests verify configuration rejection, not a claim to compiler mutation testing. The process-global panic hook stays untouched to preserve independent-call policy; it may print diagnostics.

## Watch out
Forgetting a caught panic payload deliberately leaks that exceptional payload; normal calls do not leak. This tradeoff prevents arbitrary destructor code from escaping the boundary. No new firmware commands, product loading, profile/domain merging, new native target support or physical claims are introduced. One successful Claude review plus verified supported fixes satisfies this stage; do not relabel the later tested head as the original reviewed SHA or automatically invoke a second review.
