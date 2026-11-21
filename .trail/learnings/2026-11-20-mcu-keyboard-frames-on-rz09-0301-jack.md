---
title: MCU keyboard frames on RZ09-0301/0421/0423/0427
author: Jack
date: 2026-11-20
branch: feat/mcu-frames-0301-0421-0423-0427
---

## Decided
- RZ09-0301, 0421, 0423 and 0427 (`0245`, `028A`, `028B`, `028C`) share one keyboard-only frame contract:
  - Six `03/0B` rows, rows 0..5, `[FF,row,0,15,RGB x16]` (52 bytes, unpadded).
  - Activation is `0F/02 [0,5,8]`. It counts only when `0F/82` payload[2]==8.
  - Restore goes through the legacy `03/0A` setter (`UsesLegacyReviewedMcuEffectSetter`).
  - The gate is `SupportsReviewedMcuKeyboardFrames` in `ExperimentalBladePort.McuKeyboardFrames.cs`.
- Journal the baseline before the first write, for these models too. `ExperimentalLightingBaselineJournal.IsRestorable` refuses mode 8, Spectrum, modes 5/6/7 and incomplete readings. On 0421 a Static reading decodes as unknown, so it is refused.
- Monitoring: read `0F/82` once before every frame batch that does not activate (both grid and uniform). On any mode other than 8:
  - fault with "ExternallyChanged";
  - no further write and no restore; the journal stays pending.
  - The normal restore path makes the same check first.
  - Recovery is an explicit built-in selection through `03/0A`, which clears the custom flag.
- Grid effects: Fire, Wheel, Starlight and Tidal only. Ambient Awareness and Audio Meter use uniform frames. Breathing, Wave and Spectrum keep their built-in setters.
- After the legacy setter, reread brightness and write `03/03` only if the setter changed it.

## Tried and ruled out
- Restoring after a GPIO bit-8 event, as the review JSON's `gpioMitigation` suggests. The event copies the saved records over the active ones without clearing the custom flag, and another controller can also select an effect, so a restore could overwrite another controller's choice.
- `0F/03` rows: their LED4 rows overflow the logo buffer. `[0,0,8]` also arms the LED4 logo, and storage 1 persists the saved bank.
- Reactive, Ripple and sequences on these four: no reviewed input-to-light map exists (`KeyboardLightingLayoutId` is null).
- Frames on RZ09-0509/0510: they share the row transport, but the LampArray AutonomousMode handler selects the displayed buffer. They are recorded as `NotEstablishedLampArrayAutonomousModeSelectsDisplayedBuffer`.

## Watch out
- The KI8110 (RZ09-0529), PI4110 and SO6110 images also carry LampArray HID collections. Before admitting or extending frames on any of them, check how their AutonomousMode handler interacts with the displayed buffer.
- Ambient and Audio owners select mode 8 only with their first uniform frame (`UniformFrameActivated`). Do not run the mode-8 monitor before that frame.
- None of this is physically validated. The GPIO and other-controller cases are simulation only.
