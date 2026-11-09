---
title: Harden parity readers without normalizing existing merge behavior
author: Jack
date: 2026-11-08
branch: docs/rust-boundaries-parity
touches:
  - ".editorconfig"
  - ".gitattributes"
  - docs/rust-boundaries.md
  - tests/Fixtures/Parity/README.md
  - tests/Fixtures/Parity/v1/profile-policy.json
  - tests/Fixtures/Parity/v1/razer-report.json
  - tests/OpenBlade.Protocol.Tests/ReportParityTests.cs
  - tests/OpenBlade.Service.Tests/ProfilePolicyParityTests.cs
  - tests/OpenBlade.Testing/ParityFixture.cs
  - tests/OpenBlade.Protocol.Tests/ParityFixtureTests.cs
---

## Decided
PR1's real Claude advisory review targeted base123936f3ea69881d21bd16a7a09529930c9a2c6f through head652334a1e6c8ddd624794bc8ba73c78a6aa2d849 and completed successfully. Harden the test-only JSON readers: require the pinned provenance revision, exact field names, numeric JSON values, unique fields/IDs and canonical uppercase hex. Keep theory arguments to fixture IDs. Pin encoding payload-size precedence when both payload and destination lengths are invalid, plus the accepted legacy Maximum + Fixed/5400-RPM shape. Shared JSON uses explicit LF rules.

## Tried and ruled out
Do not normalize the contradictory fan-fields pure-merge vector: that changes the behavior parity must preserve. Rename it and state that whole-profile validation rejects it. Do not expand this bounded merge slice to GamingModeKeyPolicy, CustomPerformance, GpuClockTuning or Lighting merely because their existing C# override branches are outside the fixture projection.

## Watch out
Claude reviewed the original head only; the follow-up fixes are verified by Codex source inspection and affected tests, not retroactively Claude-reviewed. Future Rust readers must consume the same strict fixture contract. Synthetic invalid merge input is not a valid hardware request or capability evidence.
