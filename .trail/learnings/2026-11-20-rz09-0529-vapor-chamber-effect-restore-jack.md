---
title: RZ09-0529 Vapor Chamber effect restore contract in KI8110 MCU 1.07.00
author: Jack
date: 2026-11-20
branch: claude/bios-ec-lighting-controls-86760e
cost: { minutes: 8, tokens: 112953 }
---

## Decided
The exact KI8110 MCU 1.07.00 image (hex SHA-256 18392850..., decoded C0EBB3B4...) supports a closed bank-0 save/restore contract for the Vapor Chamber (LED38): read it with 0F/82 [0,38,...] and restore it with 0F/02 [0,38,mode,...]. Effect records are 0x40 bytes at 0x100015D6 + 0x40*i (i 0 = LED5, 1 = LED4, 2 = LED38), selected by 0x65B4 (selector 0 = active, 1 = persistent). Bank 0 is volatile: the 0x9D68 tail persists only when the selector is nonzero. Restorable VC baselines are Off (0), Static (1: count at [5], RGB at [6..8]), Breathing (2: count 1/2/0 plus colors) and Wave (4: direction at [3], speed fixed at 40). From custom mode 8, each of these setters clears the custom-ownership flag 0x10000125[2] and skips the same-mode shortcuts. None of them changes record brightness (+2). To select keyboard and VC custom frames, send 0F/02 [0,5,8] and 0F/02 [0,38,8]. 0F/02 [0,0,8] also switches LED4 (logo) to custom.

## Tried and ruled out
Starlight (7) stores payload[3] in rec+0xA, which the 0F/82 getter does not return. Spectrum (3) is gated by 0x100001C9 and writes the hidden flag 0x10000119. Mode 9 stores parameters outside the record. Custom (8) pixels cannot be read back. All four stay unrestorable. Ghidra headless works with -loader IntelHexLoader -processor ARM:LE:32:Cortex (Cortex-M Thumb, RAM 0x10000000). The switch helper 0x4780 computes its target as LR-table + 2*byte. Separate seed lists with something other than commas, because cmd splits analyzeHeadless .bat arguments on commas.

## Watch out
Admit VC rows only when a fresh 0F/82 [0,38] read shows one of the four restorable modes with complete parameters; otherwise keep frames keyboard-only. FUN_4B80(0x26) shows overlay data instead of host pixels while the 0x4BD4/4BD8/4BDC conditions hold; LED5 has the same condition. This is source review only; physical validation is still pending.
