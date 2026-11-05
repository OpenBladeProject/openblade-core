---
title: Silent register readiness and corrected MCU manual confirmation
author: Jack
date: 2026-11-05
branch: feat/battery-saver-low-fan-validation
touches:
  - docs/so6120-direct-duty-safety.md
  - docs/so6120-ec-register-semantics.md
  - tools/firmware/profiles/so6120-ec-1.04.json
  - tools/firmware/profiles/so6120-mcu-1.07.json
---

## Decided

Use the offline CLI on the pinned SO6120 EC1.04/MCU1.07 images to distinguish mapped readback candidates from internal state. Bank0 B2/B3 are read-only aliases for cycle byte F01841; E7 exposes the low byte of802AA whose bit0 selects the FE fallback source. Last-normal8034F and saved modes86062/86063 have no exact-address entry in the full2048-entry map. Annotate these facts without inferring another access route or exact restoration.

## Tried and ruled out

Correct the older manual-readback claim in docs/so6120-direct-duty-safety.md and the ignored MCU-RESTORATION-REVIEW.md: successful manual write at4C82..4C88 sets2<<15, pendingbit16 for DC/78/DE/79/DD/DF reads. Index36 manual-state readback uses separatebit18. A matching ordinary GET can remain cached intent. The watchdog feed atB018 ends in unconditionalCPSIE atB024; it cannot preserve a blanket IRQ-masking ownership assumption. Recovered priority setup6094 via28EC assigns timer16 logical3, I2C15 logical0, USB22 logical1. These are static configuration facts, not observed overlap or complete later-state coverage.

## Watch out

MCU100000xx annotations are internal RAM, not host-readable EC indices. Generic reads still alter shared transport setup, and the access gate is itself stateful. Next audit the timer dispatch gates and every caller of5864/58B0/2520 for a defensible ownership interval, then independent two-layer bus confirmation and exact restoration. An attended experiment needs justified recovery if its host/USB path fails; an unattended fan-stop switch additionally needs independent bounded cooling handback. Existing protections do not prove fan restart or fresh sensors. Prior RPM authorizations do not authorize this different generic-register path. No live access occurred.
