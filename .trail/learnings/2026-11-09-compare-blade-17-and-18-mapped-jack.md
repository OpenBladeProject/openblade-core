---
title: Compare Blade 17 and 18 mapped Auto with the supervised Blade 16 trial
author: Jack
date: 2026-11-09
branch: feat/fan-stop-model-expansion
touches:
  - docs/fan-stop-reader-and-ec-followup.md
---

## Decided

Compare the exact three-fan DI780/KI890/KI8100/KI8110 images against the physically exercised SO6120 EC 1.04 mapped transaction. All five have identical IRQ 71 setup, acknowledgement and handler, 128-byte A1 branch/tail and final wrapper clear. The relocated IRQ vector and command handler call edges match. Blade 18 has the same A0 loop bytes; DI780 uses different registers for the same fields. The pinned RZ09-0581 trial journal contains 238 completed A0 and four completed A1 publications, with A0 readback after each A1 and no D0. It confirms Stop and Auto for that device only.

## Tried and ruled out

The KI890 2.09 PchSetup consumer reads the default-store variable into two DXE policy blocks. Following the block GUIDs through the retained DXE and SMM modules identifies NHLT audio and PCI device override handling. This trace did not find the fixed Super I/O decode write. Public exact-model motherboard photos reviewed this turn were too low resolution to identify the EC part number.

## Watch out

Matching mapped completion code makes the existing A0/A1 protocol a better candidate once third actual PWM has a separate reader. It does not establish D0 callback drainage, identical silicon interrupt delivery, live port availability, or ownership. The SO6120 journal records the reviewed source identity, not a new running-image attestation. Keep any future hardware validation limited to the remaining exact-model uncertainty.
