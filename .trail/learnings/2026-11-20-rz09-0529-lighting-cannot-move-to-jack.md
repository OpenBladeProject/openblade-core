---
title: RZ09-0529 lighting cannot move to BIOS or EC
author: Jack
date: 2026-11-20
branch: claude/bios-ec-lighting-controls-86760e
cost: { minutes: 2, tokens: 82361 }
touches:
  - AGENTS.md
  - docs/rz09-0529-lighting-expansion.md
  - evidence/source-reviewed-blades/2026-11-13-rz09-0529-keyboard-frames.json
---

## Decided
BIOS/EC is not a route for RZ09-0529 (1532:02C7) lighting. The KI8110 keyboard MCU owns all lighting, and OpenBlade already drives it for effects, per-key frames, brightness, logo, VC enabled and touchpad status. The gaps left by PR 977 (VC RGB frames, VC sleep policy, touchpad RGB) are blocked by missing read/restore semantics, not by the transport.

## Tried and ruled out
EC 1.06 (ITE8320, NDS32) enumerates as 1532:F003. Its host dispatch rejects class 0F, and classes 02/03/04/0A are no-ops, so MCU LED38 commands cannot be sent to it. Its indicator branch only stores keyboard status LEDs. On RZ09-0330, BIOS ACPI exposes only generic EC transfers with no keyboard target or state contract; using them would be raw firmware access, which AGENTS.md forbids. Sources: docs/rz09-0529-lighting-expansion.md and evidence/source-reviewed-blades/2026-11-13-rz09-0529-keyboard-frames.json (ecTouchpadFirmwareReview).

## Watch out
The promising follow-up is on the MCU: the 0F/82 [0,38,...] getter reads built-in VC effect parameters. If a matching region-38 effect setter is established, VC row 6 could be admitted in frames when the baseline is a built-in effect, and refused when it is unknown or custom. Nothing establishes that setter yet.
