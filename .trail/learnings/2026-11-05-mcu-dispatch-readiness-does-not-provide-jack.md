---
title: MCU dispatch readiness does not provide shared bus ownership
author: Jack
date: 2026-11-05
branch: feat/battery-saver-low-fan-validation
touches:
  - docs/so6120-direct-duty-safety.md
  - tools/firmware/profiles/so6120-mcu-1.07.json
  - docs/so6120-mcu-transfer-ownership.md
---

## Decided

The SO6120 MCU1.07 ownership audit found30 direct read-helper calls,24 direct write-helper calls and2 calls to the underlying transfer routine in the recovered application listing. Eight recovered outer callers include periodic non-EC peripheral writes from A8F8, invoked at66E4 immediately before thermal worker4B5C at66E8. Both use descriptor100000D4 and buffer1000024C. Pausing thermal pending work alone would not establish whole-transport ownership. Record the scoped inventory in docs/so6120-mcu-transfer-ownership.md and add five internal-state annotations to the explorer.

## Tried and ruled out

Command-ready10000148 is set by the90-byte report branch at24F2, consumed by timer-dispatched9598 and cleared at963A. Initialization phase100001E9 progresses1/2/3 and gates timer work at>=3. Targeted Thumb decoding resolves1000014D asUSBconfiguration: request8 returnsit,request9 reaches3F4C,3F6C stores the accepted descriptor value and3FB2 clearsit. These gates do not acquire transfer ownership or drain pending work. Main bus clients execute before its later14D/E8/14E checks. A host-side mutex cannot serialize those firmware contexts. No actualcollision was observed.

## Watch out

Keep100001E8 neutral: complete state meanings and producer coverage remain unresolved. Scope call counts to recovered direct operands; computed and unrecovered calls are not proven absent. Treat inline switch tables and literal pools as data. The helper wait's watchdog feed re-enables interrupts; no generation ties shared completion to an initiating client. Further live generic reads still require justified transport ownership, and direct duty additionally needs exact restoration and credible recovery. Existing generic gateC1 supplies none of those properties. Ghidra refinement used isolated temporary project/settings, documented launcher JVM options and read-only raw import; no firmware was executed or existing project modified.
