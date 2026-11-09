---
title: MCU 1.07 exposes generic EC access but rejects the normal duty API
author: Jack
date: 2026-11-05
branch: feat/battery-saver-low-fan-validation
touches:
  - docs/silent-low-fan-validation.md
  - docs/so6120-ec104-fan-analysis.md
  - docs/so6120-mcu107-ec-fan-bridge.md
---

## Decided
The SO6120 MCU 1.07 image closes the EC transport gap: normal1532:02E0 thermal requests queue MCU work that writes the EC banked mode/manual/RPM fields through I2C. Class00 also exposes gated generic EC read/write transfers. This supersedes the earlier unresolved transport link; the separate EC1532:F003USB descriptor is not evidence of isolation. Exact hashes, instructions, field mappings and independent verification are in docs/so6120-mcu107-ec-fan-bridge.md. Static reachability is established; a thermally supervised fan-stop capability is not.

## Tried and ruled out
The pinned base-device duty wrappers actually send0D/0D and0D/8D, despite misleading unused0E/8E named constants. ExactMCU1.07 rejects both actualduty commands, and normalmode SET rejects selectors above7. Direct EC duty requires the generic register path, a separate MCU gate and EC permission, and bypasses the ordinary curve. Generic register access does not provide a fan-specific bound or exclusive ownership. No hardware operation was needed to prove these facts.

## Watch out
The normal worker does not consult the raw-transfer gate or force the shared EC bank before each operation. Ordinary mode/target GETs return MCU cache; matching reads do not prove a laterECwrite completed. Raw register reads, PWM duty and actual tachometerRPM remain different evidence. Protection coverage, preservation of Silent power behavior, pending-work reconciliation, bank/flag restoration and host-loss handback remain unresolved. Do not translate this research into a raw-write interface or new experiment. Preserve exactmodelscope and withheldproductionadmission; liveSilentcomparisonremains paused. No runtime code, installedstate or hardware settings changed.
