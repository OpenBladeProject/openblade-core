---
title: SO6120 host mailbox bypasses MCU state but provides no fan lease
author: Jack
date: 2026-11-05
branch: feat/battery-saver-low-fan-validation
touches:
  - docs/so6120-mcu-transfer-ownership.md
  - tools/firmware/profiles/so6120-ec-1.04.json
  - tools/firmware/profiles/so6120-mcu-1.07.json
  - docs/so6120-host-mailbox-alternatives.md
---

## Decided

The exact SO6120 EC1.04 updater block3 exposes host ports 0x300/0x301 through InpOut imports. Its A0/A1 register mailbox matches F02290 bank, 91 index, 92 count, 93 command, 94 status and A0..BF data. It bypasses MCU transfer descriptor100000D4 and takes a request-specific bank, but does not establish an admitted OpenBlade transport or exclusive field ownership. Record the source and limits in docs/so6120-host-mailbox-alternatives.md and the offline profile findings. No hardware access or vendor execution occurred.

## Tried and ruled out

MCU command00/04 value5 writes application state100001E8 and opens generic gateC1; normal thermal and peripheral bus clients remain eligible. GET00/84 is not a drain acknowledgment. The vendor mailbox wrapper replaces fields without an idle acquisition and polls command zero without a timeout. EC status80 precedes the mapped loop and survives ignored read-only writes. EC event dispatcher2115E invokes slot13 at2E688=CD64, then12D80/203C, and clears the command. This is not exclusive ownership.

The updater name Enable_Finite_Life_time is misleading for this question: it writes command22 through legacy ports0x302/303. The matching guarded EC branch132BC calls16992 then1679A, persisting five record bytes via flash erase/program. Separate countdown8044E clears legacy mailbox state after commandF02212 is idle; FE bypasses the ordinary countdown check. Neither supplies a host-loss fan lease.

## Watch out

Keep application-state names neutral and scope negative conclusions to reviewed handlers. In A8F8, A96C tests state after the A95C transfer; the A98A alternative bypasses that test. Per-request mailbox bank selection does not prevent a mapped write to shared bank index0F. Host mailbox reads still write an I/O index. Do not execute the updater, load InpOut, copy raw-port access into OpenBlade, or treat a method name, transport success or mailbox timeout as thermal recovery. Exact restoration, supported transport ownership and cooling recovery remain missing for direct duty. These are SO6120-specific static findings, not other-model admission or physical results.
