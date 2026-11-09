---
title: Recover and compare Razer Blade 17 EC 1.03 from the archived official link
author: Jack
date: 2026-11-09
branch: feat/fan-stop-model-expansion
touches:
  - docs/fan-stop-bram3-readback-review.md
  - docs/fan-stop-reader-and-ec-followup.md
---

## Decided

Archived exact-model Razer support pages can recover retired official download links even when the live article presents an Oracle redirect page. The 2022 snapshot of article 6092 gives a still-hosted Razer-signed carrier for RZ09-0423, SSID 1A583005, DI780 EC 1.03. The signed input and extracted image pins are in docs/fan-stop-bram3-readback-review.md.

## Tried and ruled out

EC 1.03 and 1.05 have byte-identical D0 copy and inner-clear paths, primary interrupt setup and acknowledgement, final wrapper clear, A1 mapped-write branch, legacy raw reader, both competing descriptor cleanups, and FE third-duty store. Both exact 2048-entry maps lack F01808 and contain a zero byte at misdirected address 001808. Using the older firmware would preserve the same readback gaps.

## Watch out

The archive replay may return a gzip body without a usable content-encoding header. Parse it as data and verify the separately obtained vendor signature and exact package selector. Similar code spans do not admit a new live firmware version or authorize an upgrade or downgrade.
