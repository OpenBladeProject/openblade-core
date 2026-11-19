---
title: Distinguish upstream lighting setters from firmware state readback
author: Jack
date: 2026-11-13
branch: fix/issue959-lighting
---

## Decided
Treat OpenRazer and OpenRGB setter support as evidence for commands and declared geometry, separately from independent firmware readback. The pinned 2026-11-13 audit identifies exact 1532:0253 support in both projects and matches the other eight independent-control profiles. Keep the exact-model Synapse brightness/logo implementation; no upstream setter alone closes its keyboard-effect recovery gap.

## Tried and ruled out
OpenRazer getEffect/getEffectColors/getEffectSpeed return daemon zone cache. Its restore_effect replays that cache or persisted settings, and the kernel matrix effect attributes are write-only. OpenRGB mode and RGB state likewise reside in application memory; its direct controller path does not save and independently restore an arbitrary firmware effect baseline. Command ACKs do not establish active effect or emitted light.

## Watch out
Pin immutable upstream commits and source hashes. Keep geometry separate from country-specific key maps, and do not copy retries, default effects or a software cache into OpenBlade confirmation. The upstream alternate brightness helper does not require replacing the already reviewed Synapse contract. See the source-reviewed upstream lighting audit for exact sources and bounded conclusions.
