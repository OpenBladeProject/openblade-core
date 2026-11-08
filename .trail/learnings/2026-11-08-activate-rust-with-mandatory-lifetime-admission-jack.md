---
title: Activate Rust with mandatory lifetime admission and lossless host keys
author: Jack
date: 2026-11-08
branch: feat/rust-production-activation
touches:
  - docs/developer-guide.md
  - docs/rust-boundaries.md
  - rust/README.md
  - src/OpenBlade.Contracts/IpcMessageCodec.cs
  - src/OpenBlade.Core.Native/NativeProfilePolicy.cs
  - src/OpenBlade.Service/ApplicationProfileResolution.cs
  - tests/OpenBlade.Core.Native.Tests/NativeProfilePolicyTests.cs
  - tests/OpenBlade.Core.Native.Tests/OpenBlade.Core.Native.Tests.csproj
  - tests/OpenBlade.Service.Tests/OpenBlade.Service.Tests.csproj
  - tests/OpenBlade.Service.Tests/ProfilePolicyParityTests.cs
---

## Decided
Stage 7 activates the packaged Rust codec, then selected profile merge policy in separate commits stacked on 69f66ba673c0523c0521b09b286a7d09f8137488. Earlier stage-specific dormant/loading restrictions are superseded only for this authorized production activation. Keep C# Windows UI/service, both v1 native ABIs, IPC and saved schemas unchanged.

The mandatory runtime loads a fixed application-base artifact admitted by the managed assembly's embedded target/version/ABI/hash manifest. Retain file lease and modules through process exit so shutdown handback still works. Sign release native bytes before embedding their hash; ordinary builds require pinned Rust plus native linker/SDK.

Profile keys are opaque C# UTF-16, not UTF-8 schema fields. Project at most four one-byte source-occurrence tokens and resolve back to original host string references; Rust only selects them. This losslessly handles unpaired surrogates, empty/long keys and equal text while preserving spelling. Retain null branches, curves, untouched fields and legacy exceptions.

## Tried and ruled out
Do not compare Rust against activated production Rust and call that parity. Frozen test-only managed report/profile baselines retain predecessor behavior and unchanged canonical fixture provenance; no product fallback exists.

Do not reject legacy surrogate keys, normalize profiles, add an intermediate hardware transition, or replay after possible submission. Actor native-component failures complete the claimed item, poison/drain queued work and release handles/lease. Two fresh-process offline probes dispose the real codec after a mocked first SET to verify batch and response failure without hardware.

## Watch out
Source-reviewed support and software tests do not establish physical validation. Native CI covers the pure boundary on four native hosts; retained Windows production consumers have Windows integration coverage. Startup must precede hardware composition, and release packaging must compile the hash binding after native signing. Trail check's generic build/profile/JSON matches do not show fixture regeneration, raw diagnostics, new model authority or schema changes.
