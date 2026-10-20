---
title: Apply reviewed thermal ACK contracts across every typed caller
author: Jack
date: 2026-10-19
branch: fix/739-offline-controls
touches:
  - CHANGELOG.md
  - docs/blade18-observer-evidence.md
  - src/OpenBlade.Protocol/ExperimentalBladePort.Custom.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.Fans.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.HyperBoost.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.cs
  - tests/OpenBlade.Protocol.Tests/ExperimentalSourceBackedAckWireTests.cs
  - tests/OpenBlade.Protocol.Tests/ExperimentalThermalExpansionTests.cs
  - tests/OpenBlade.Protocol.Tests/SourceBackedPerformanceAckTests.cs
---

## Decided
The reviewed 029F/02A0 bounded ACK contract must cover every existing caller of the same thermal SET: performance, Custom entry, fan mode, custom levels, and admitted 029F HyperBoost/Maximum Fans cleanup. A response-only correction must not leave stricter copies in another typed operation. Source pins and exact helper offsets are in docs/blade18-observer-evidence.md.
## Tried and ruled out
Fixing only ExperimentalBladePort.Fans.cs and the Maximum Fans toggle leaves performance and custom-level operations with the same source-contract mismatch. Do not broaden models or unrelated commands to solve this consistency problem.
## Watch out
0D/02 ACK length is bounded to four bytes, 0D/01 and 0D/07 to three, and 07/0F to one. Every caller keeps independent GET confirmation and the existing per-write guards. No commands, arguments, ordering, source/pad checks, shared-mask semantics or pause/no-replay behavior change. Source-backed ACK handling is not physical validation.
