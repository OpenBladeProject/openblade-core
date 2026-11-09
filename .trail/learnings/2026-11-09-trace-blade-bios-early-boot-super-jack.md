---
title: Trace Blade BIOS early boot Super I/O decode
author: Jack
date: 2026-11-09
branch: feat/fan-stop-model-expansion
touches:
  - docs/fan-stop-reader-and-ec-followup.md
---

## Decided

For Blade 17/18 Super I/O decode, reconstruct the signed BIOS PFAT `FV_BB` and inspect the `OemBoardPei` TE image GUID `E90CF8BD-D50C-40D5-9D5F-1F8D340B86C8`. DI780 2.01, KI890 2.04/2.09, KI8100 1.06 and KI8110 4.01 all select `2E` on the entry path and enable bit 12 of PCI `00:1f.0` offset `82`. All five then initialize the two secondary mailbox registers through `302/303`. See `docs/fan-stop-reader-and-ec-followup.md` for module hashes, offsets and different chipsets' write paths.

## Tried and ruled out

Searching AML call sites and DXE PE32 modules alone missed the early boot writer. In a TE section, map each original raw pointer with the adjustment `40 - StrippedSize` and verify the entry against the recovered code section. The KI890 native USB class 00 staged echo and class 0D thermal getters do not expose actual PWM3. The adjacent Charlotte2 updater targets only RZ09-02886/02887/02888 and SSID `1A582002`.

## Watch out

Boot initialization is source evidence. Verify post-boot decode on the exact host, the indirect binding to PWM3, exclusive nested selector ownership, and command completion through Auto before connecting the live guardian. Public hw-probe lspci logs use verbose or machine-readable summaries and omit the raw word. Use the narrow PCI-only read in the follow-up for the first installed check. Do not infer support for a neighboring model from a similar image name.
