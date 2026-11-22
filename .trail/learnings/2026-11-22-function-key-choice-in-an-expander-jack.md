---
title: Function key choice in an Expander header toggled the card instead of selecting
author: Jack
date: 2026-11-22
branch: fix/driver-keyboard-deferred-subscription
cost: { minutes: 13, tokens: 246459 }
touches:
  - CHANGELOG.md
  - src/OpenBlade.Tray/ResponsiveChoiceSelector.xaml
  - src/OpenBlade.Tray/ResponsiveChoiceSelector.xaml.cs
  - tests/OpenBlade.Tray.Tests/CapabilityPresentationTests.cs
---

## Decided
On 2026-11-22 the RZ09-0581 "Function key behavior" choice could not be changed with the mouse. A click opened or closed the card, and the choice never changed. The ResponsiveChoiceSelector in the Expander header uses a SelectorBar, whose items select on release and leave the press unhandled. The press bubbles to the header ToggleButton, which captures the pointer and toggles the card. UI Automation Select() worked, greying the control for about 200 ms while applying, which proved the service path was fine. The shared selector now marks PointerPressed handled on its SelectorBar, so the choice stays nested in its card.
## Tried and ruled out
A greyed control is not proof of a failed write. CanEditFunctionKeyPrimary drops only while the one-shot apply is in flight. The keyboard-driver RecoveryRequired state and the Windows-hook function-row backend do not gate this firmware control on 0581.
## Watch out
Any interactive control placed in an Expander header must consume its own press. ToggleSwitch and ComboBox already do, and a SelectorBar does not. Source-level XAML contract tests cannot prove pointer routing, so check a real mouse click in the rendered Tray.
