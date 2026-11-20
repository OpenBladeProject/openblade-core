---
title: Native manual-external display power and checkpoint source 3
author: Jack
date: 2026-11-20
branch: feat/native-reconcile-main-ipc
touches:
  - docs/windows-rust-development-service.md
---

## Decided
- Native Agent ManualExternalPowerPresent: PowerAdmission keeps a manual_external flag only while the source is unconfirmed; a flag change starts a new generation (managed MarkUnavailableCore). capture() stays confirmed-only for profile apply and automation; capture_manual() returns source 3 for the manual setters (brightness, HDR, refresh, color).
- write::power_refusal is the single ValidateExpectedAsync mapping: a manual-external context refusing a confirmed request reports the UNCONFIRMED text, not CHANGED.
- Recovery checkpoint: source 3 loads and saves only as ManualHold with no application, baseline or owned field (managed IsRecoverySourceAdmitted). Loaded 3 means last = None; persist writes last.unwrap_or(3). A manual-external begin never overwrites the last confirmed source.
- Native profile schema 27 / override schema 10 read older 25-26 / 8-9 with managed lighting rejections; USB-C admits every AC preset (Power::external).

## Tried and ruled out
- Treating "no checkpoint and no confirmed source" as a CHECKPOINT refusal: main now saves an ExternalUnknown hold and then refuses with the unconfirmed text. Three tests were updated for that.

## Watch out
- Session Display IPC is still version 2 although main changed its meaning; a pre-change peer fails closed (documented in docs/windows-rust-development-service.md, Known compatibility gaps).
- Unclassified external thermal control (f6502f85) is still not ported to the native Service.
- The managed checkpoint fixture now has 11 rows; the Rust loader test asserts the count.
