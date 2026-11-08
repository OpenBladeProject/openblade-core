---
title: "Pin Rust migration boundaries before replacing pure C# helpers"
author: Jack
date: 2026-11-08
branch: docs/rust-boundaries-parity
touches:
  - docs/developer-guide.md
  - tests/OpenBlade.Protocol.Tests/OpenBlade.Protocol.Tests.csproj
  - tests/OpenBlade.Service.Tests/OpenBlade.Service.Tests.csproj
  - docs/rust-boundaries.md
  - tests/Fixtures/Parity/README.md
  - tests/Fixtures/Parity/v1/profile-policy.json
  - tests/Fixtures/Parity/v1/razer-report.json
  - tests/OpenBlade.Protocol.Tests/ReportParityTests.cs
  - tests/OpenBlade.Service.Tests/ProfilePolicyParityTests.cs
  - tests/OpenBlade.Testing/ParityFixture.cs
---

## Decided
PR 1 of the Rust migration is a documentation and shared-fixture baseline only. The future rust/ workspace separates openblade-types, openblade-protocol, openblade-domain and openblade-ffi. The C# service remains the sole hardware owner in phase 1; ABI, IPC, persisted schemas and fixture schemas are separate contracts. Shared UTF-8 JSON cases live in tests/Fixtures/Parity/v1, consumed by existing C# test projects and later by Rust directly.

## Tried and ruled out
Do not generate expected parity values with production code at test runtime or copy software vectors into physical admission evidence. This stage implements no Rust workspace, loading, transport or hardware changes. The profile suite pins ApplicationProfileResolution merging only, not complete profile validation, application selection or recovery.

## Watch out
False and zero override values differ from absent/null; a present application override normalizes a missing display baseline. Legacy fan modes synthesize existing default fan-control settings. Keep exact numeric validation-failure meanings and response-fault precedence. New OS backends require reviewed admission and software failure/recovery checks; track physical validation separately and never inherit Windows physical results. The proposed C ABI still needs its exact header/layout and native-boundary tests in its implementation stage.
