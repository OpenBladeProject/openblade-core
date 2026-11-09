---
title: Keep configuration schema discovery separate from strict decoding
author: Jack
date: 2026-11-09
branch: feat/rust-config-recovery
touches:
  - src/OpenBlade.Service/BatteryConfigurationStore.cs
  - tools/openblade-windows-host/src/configuration.rs
  - tests/OpenBlade.Service.Tests/WindowsReplay/v1/configuration-inspection.json
---

## Decided
Separate permissive schema discovery from strict payload-envelope auditing. The managed JsonDocument can read schemaVersion beside an unrelated lone surrogate escape. Preserve numeric future-schema precedence over duplicate or undecodable members inside a syntactically readable document.

## Tried and ruled out
Strict decoding in the discovery pass hid forward-schema evidence. Recursively reparsing raw subtrees repeated the same work at every nesting level. Use one bounded syntax/depth pass and a streaming duplicate audit. Treat the quoted profile schema and undecodable property-name cases as preserve outcomes in Rust, while the shared oracle records the managed InvalidOperationException.

## Watch out
The shared inspection vectors are checked by both readers. The fixed transaction marker digests are public; their spelling and exact source bytes matter for compatibility. Trail lexical matches on earlier protocol and hardware stages do not apply to this test-owned transaction reader.
