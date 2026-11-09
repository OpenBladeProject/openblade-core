---
title: Silent Automatic and host fan curves use different control paths
author: Jack
date: 2026-11-05
branch: feat/battery-saver-low-fan-validation
touches:
  - docs/silent-low-fan-validation.md
---

## Decided
For the continued fan-stop investigation, focus on Silent as requested and set the proposed Battery Saver A/B/A aside. Distinguish the shared USB command encoding from each thermal profile's firmware behavior. The same pinned Product 736 policy module 50779 maps auto to manual flag 0 and both manual and smartFanCurve to flag 1. Module 91058 returns after actual fan-speed reads in Automatic; its host-driven custom curve selects temperature-dependent targets and writes the ordinary class-1 thermal RPM setter.

## Tried and ruled out
A custom curve is not a separate demonstrated fan-off mechanism or a bypass of the observed Silent/manual floor. In the inspected source paths no setter for firmware Automatic's stop temperature was found. The prior Silent zero and 1500 measurements remain negative for forced fan stop, while its Automatic idle baseline already demonstrated zero rotation. Do not repeat either Battery Saver or Silent hardware writes merely because the user asks what might be possible.

## Watch out
The remaining practical Silent research is the relationship between Automatic idle stopping, temperatures, GPU activity and power use. A lower-heat policy would be a hypothesis to validate and could support a preference for silence; it cannot be advertised as guaranteed fans off. A different forced-off mechanism would need its own exact-model firmware evidence. No new probe was implemented, no hardware test occurred, and no production capability changed during this static follow-up.
