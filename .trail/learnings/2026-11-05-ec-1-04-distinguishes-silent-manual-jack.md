---
title: EC 1.04 distinguishes Silent manual clamp from Battery Saver zero target
author: Jack
date: 2026-11-05
branch: feat/battery-saver-low-fan-validation
touches:
  - docs/silent-low-fan-validation.md
  - docs/so6120-ec104-fan-analysis.md
---

## Decided
Static decoding of the user-supplied SO6120 EC 1.04 image establishes a mode-dependent low-target rule. In the admitted normal manual path, routine 0x15C62 maps any below-minimum request to zero only for selector 3; other normal selectors use the table minimum. Existing exact-model vendor evidence maps Battery Saver to 3 and Silent to 5. All four selector-5 factory tables specify 2000 RPM minimum. Silent Automatic has a separate zero-target stage with multiple input bounds and timing. See docs/so6120-ec104-fan-analysis.md for image hashes, exact offsets and independent instruction/data review.

## Tried and ruled out
Literal zero and smaller positive targets are not a new Silent bypass: the same manual minimum applies. Shared report geometry does not make Battery Saver and Silent equivalent. The compiled FE direct-duty branch is a real research lead, but its unresolved gating bit and the EC USB descriptor 1532:F003 do not establish a bounded command through the admitted 1532:02E0 controller. No raw mode, memory write or flash experiment follows from these internal offsets. DTT vaults yielded no separate fan-control contract.

## Watch out
Factory table bytes are not a live active-table dump; persistent tables and overrides can differ. The Automatic loader can force targets up to 3000 under unresolved flags, and raw channel bounds must not be labeled CPU/GPU Celsius thresholds. New static evidence supplies a mechanism consistent with the earlier Battery Saver stop, but does not replace integration, restoration or lifecycle validation. Keep production fan-stop admission withheld and retain the exact-model scope. This analysis involved no hardware access or vendor executable execution; the user's live Silent comparison remains paused.
