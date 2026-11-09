---
title: Blade 17/18 interrupt destinations and receiver acquisition
author: Codex
date: 2026-11-09
branch: feat/fan-stop-model-expansion
touches:
  - docs/fan-stop-reader-and-ec-followup.md
  - docs/fan-stop-model-coverage.md
---

## Decided

The interrupt-rooted census accounts for the retained computed stores on DI780, KI890, KI8100 and KI8110. It includes the KI890 SMBus table and stack paths and 94 scalar stores omitted by the older function inventory. The bounds assume the retained roots, intact stack and descriptors, and a valid I2C bank. Lifecycle can clear actual duty 3 directly.

## Tried and ruled out

The four exact keyboard images reject the named duty GET and SET. The ITE IT81202 manual supplies matching BRAM comparator and IRQ behavior on a different core and chip-ID layout. It is candidate semantics only. PI490 current-speed GET returns a receiver-fed cache without a freshness token. Exact Synapse native version APIs read Windows BIOS metadata.

## Watch out

Exact-chip host-write and IRQ-drain semantics remain open. The IRQ destination proof does not establish exclusive ownership or guardian-held HID handback. For the other seven models, prioritize a known-good CH530 receiver image with chip attribution, then the other exact-board images. The separate DA530 ITE8390 listing is from a non-POST donor. DA550 and CH551 must remain distinct.
