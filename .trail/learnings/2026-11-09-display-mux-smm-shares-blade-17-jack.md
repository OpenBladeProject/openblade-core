---
title: Display mux SMM shares Blade 17 and 18 fan mailboxes
author: Jack
date: 2026-11-09
branch: feat/fan-stop-model-expansion
touches:
  - docs/fan-stop-reader-and-ec-followup.md
---

## Decided
The four Blade 17/18 families have an independent firmware mailbox caller. In DI780 2.01, KI890 2.04/2.09, KI8100 1.06 and KI8110 4.01 the TpvSmm software-SMI callback publishes primary A1 and can use the legacy request window. The expanded images all have checksum-valid SSDTs with the same MXDM method body that writes the B8 dispatch value. A live route must coordinate with the display mux or avoid the shared windows. Keep actual mux invocation and independent PWM readback distinct.

## Tried and ruled out
A zero command poll does not reserve the staged descriptor or port selector. Closing Synapse leaves this SMM path possible, and the Windows ACPI global-lock IOCTLs are internal. The raw status flags can be quiet before an already-dispatched callback returns. Four ITE8320 firmware fingerprints had no hits in retained 0370, 0482 and 0508 ROM and expanded UEFI/PSP members.

## Watch out
The reported 0370 repair photo shows IT8320VG-256 but does not show the PI411 silkscreen. It narrows acquisition toward a preserved exact donor read and is not a receiver contract. I2EC snoop behavior is documented for IT81202, not proven on the installed Blade EC. The source trace does not claim a live display-mux event or authorize any probe.
