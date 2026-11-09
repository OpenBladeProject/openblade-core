---
title: Preserve static report encoder aliased input semantics
author: Jack
date: 2026-11-08
branch: feat/rust-production-activation
touches:
  - docs/rust-boundaries.md
  - src/OpenBlade.Protocol/RazerReport.cs
  - tests/OpenBlade.Protocol.Tests/NativeActivationTests.cs
---

## Decided
Stage7 activation must preserve even the internal static encoder's aliased-span input behavior. Five concrete overlapping payload/destination cases failed against the frozen predecessor oracle before the fix.

The managed encoder cleared the destination and wrote its header before payload.CopyTo. Project only the overlapping source bytes into an at-most80byte stack buffer (header offsets1/5/6/7; other cleared bytes0), then make the single Rust encode call. Keep payload-size and exact90byte destination validation first. The normal disjoint transport path is unchanged and ABI buffers remain disjoint.

## Tried and ruled out
Do not assume reading original aliased payload before clearing is parity, or merely reject overlap that the old internal API accepted. Do not restore a managed encoder fallback, publish partial output, or issue a second native encode. No hardware command, transaction allocation or admission rule changes.

## Watch out
Span.Overlaps offset is destination start minus payload start. Replace source indices[max(0,offset),min(payloadLength,offset+90)); overlap bounds prove offset+90 safe. Independent review verified lifetime and math. The focused five regressions and one exhaustive bounded geometry test compare the entire backing buffer for all0..80payload lengths and offsets−80..90, including disjoint-touch boundaries. All3859Protocol tests pass with zero skips. These are software parity tests, not physical evidence.
