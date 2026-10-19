---
title: Accept source-backed fan ACKs while independent readback remains authoritative
author: Jack
date: 2026-10-19
branch: fix/739-offline-controls
touches:
  - CHANGELOG.md
  - docs/blade18-fn-decoder.md
  - docs/blade18-observer-evidence.md
  - src/OpenBlade.Protocol/ExperimentalBladePort.Custom.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.Fans.cs
  - src/OpenBlade.SessionAgent/Program.cs
  - src/OpenBlade.SessionAgent/Rz090484FnInputState.cs
  - src/OpenBlade.SessionAgent/ServiceReactiveLightingLoop.cs
  - src/OpenBlade.SessionAgent/WindowsReactiveKeyboardSource.cs
  - tests/OpenBlade.Protocol.Tests/ExperimentalThermalExpansionTests.cs
---

## Decided
Use pinned Product 671 and 672 vendor SET contracts for existing 029F/02A0 fan and Maximum Fans operations. Successful correlated acknowledgements may omit or differ from requested payload; bound mode to 0..4 bytes, target to 0..3, and Power Mode Control to 0..1. Immediate independent GETs and final state checks remain authoritative. See docs/blade18-observer-evidence.md for exact artifacts, hashes and inheritance.
## Tried and ruled out
The owner Observer inventory bundles contain no returned command bodies, so they cannot prove an echo mismatch caused the physical failure. Do not infer that custom CPU/GPU pairs changed by themselves, speculate about the established factory charger, or ask the owner for another broad matrix. Do not inherit this response contract into an unreviewed model.
## Watch out
Keep the whole-mask preguard, exact device checks, fresh safety admission, status/envelope/transaction validation and stop-on-ambiguity. Failed readback pauses writes; never replay or try a fallback sequence. Source correctness does not establish physical recovery; issue 739 remains open. This intentionally supersedes strict echo expectations only for the reviewed setter paths.
