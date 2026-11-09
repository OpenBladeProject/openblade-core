---
title: Blade 17 and 18 Auto needs live selector readback
author: Jack
date: 2026-11-09
branch: feat/fan-stop-model-expansion
touches:
  - docs/fan-stop-reader-and-ec-followup.md
---

## Decided
On DI780, KI890, KI8100 and KI8110 keep the captured override asserted until an independent live-selector read confirms Auto left FE. Native EC HID 0D/82 reads the live selector if F003 enumerates. Use independent restored staging and override readback before closing the journal. The reviewed PWM initializers set the cycle register F01841 to B7, so candidate indirect reads have a positive control against mapped B2 before comparing duties against B0/B1.

## Tried and ruled out
One-byte mapped A1 completion scratch retains the same count, metadata, index and changed flag after a completed Auto transition or an Auto publication cleared by the empty callback. An A0 fence preserves those bytes. The keyboard I2C path also sets the changed flag. Razer updater indirect callers access SPI registers, so they do not establish installed PWM readback. A repair photo labeled RZ09-0370 shows the APF21008 board associated with RZ09-0427 and cannot identify the PI411 receiver.

## Watch out
The firmware policy can change FE independently if override clears early. The selector getter does not prove staged duty or override restoration. Check actual endpoint enumeration and installed transport ownership. A third-duty value of zero needs a distinguishing nonzero observation before it can serve as independent evidence.
