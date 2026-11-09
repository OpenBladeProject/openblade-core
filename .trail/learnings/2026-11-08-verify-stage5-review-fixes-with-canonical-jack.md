---
title: Verify stage5 review fixes with canonical source identity
author: Jack
date: 2026-11-08
branch: build/rust-native-package
touches:
  - build/NativePayload.ps1
  - build/Publish.ps1
  - docs/rust-boundaries.md
  - tests/Build.Tests/NativePayload.Tests.ps1
---

## Decided
Claude reviewed stage5 base77cc29cf555c6a4858a78af0ab1cb90f41eb345e to original head7feb405aab17cee1142fe40378d474f0f45c4b77 with claude-opus-5-5/high and success_native_review. Independently confirm and fix the broad import allowlist, checkout-dependent source hashes, PE directory bounds, trailing-newline import match and unverified unsigned hash. Record canonical Git blob identities while freshly rejecting dirty/untracked native sources; LF/CRLF checkouts of the same revision now verify identically. Admit exactly the three observed Windows imports. Bound PE32+ directories using NumberOfRvaAndSizes and SizeOfOptionalHeader. Retain the original unsigned artifact hash through signing, verify unsigned equality, and compute the initial hash once. Tests cover source checkout conversion, PE/header/import negatives and real artifact tampering.

## Tried and ruled out
Do not add bcryptprimitives or other future imports without a concrete dependency in the actual DLL. A future HashMap use is not a current packaging defect. Repeated fresh checks across build/copy/sign/embed boundaries resolve different integrity questions; unmeasured caching work and broad arbitrary-filename support do not justify a new abstraction. Canonical source identities are required provenance, not redundant data to delete. One completed Claude review plus verified supported fixes satisfies this stage; do not attribute review to the later final head or automatically rerun Claude.

## Watch out
The first wrapper invocation failed deterministically before Claude started because of a full-SHA transcription error. Correct it once against unchanged scope; the successful corrected run is the only model review. Keep its original head, durable metadata and final tested head distinct. Generic Burn extraction now verifies actual file hashes against published sources for every payload file; verify before signing so a final-hash update cannot bless an unexpected replacement. No product loading, hardware, IPC/schema or capability change follows from these packaging fixes.
