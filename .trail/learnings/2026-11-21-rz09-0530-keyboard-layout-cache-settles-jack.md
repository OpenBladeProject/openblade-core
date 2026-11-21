---
title: RZ09-0530 keyboard-layout cache settles only device results
author: Jack
date: 2026-11-21
branch: feat/rz09-0530-regional-keymaps
touches:
  - src/OpenBlade.Protocol/ExperimentalBladePort.KeyboardLayout.cs
  - src/OpenBlade.Protocol/ExperimentalKeyboardLayoutMetadata.cs
  - tests/OpenBlade.Protocol.Tests/ExperimentalBladeFactoryTests.KeyboardLayoutMetadata.cs
---

## Decided
The per-identity keyboard-layout cache (ExperimentalKeyboardLayoutMetadata) settles a failed 00/86 read only when the result says something about the device: a status rejection, a payload-validation failure (session PayloadValidator/PayloadLength, or the port's own contract check after the reply), or a GET that left the session unhealthy. Pre-submission refusals (Guard phase, conflict or identity blocks), shared-gate admission timeouts, queued or caller cancellation, and a no-response timeout that left the session healthy stay unsettled, so the next session reads again. The rule is ExperimentalBladePort.KeyboardLayoutFailureIsDeviceResult and applies to both RZ09-0530 and RZ09-0484.

## Tried and ruled out
Settling every non-cancellation IOException as a permanent null left regional keys off until the service restarted after one transient guard refusal, and regressed RZ09-0484, which main re-reads on the next session. Reading on every open stays ruled out: it re-poisoned battery and lighting sessions after a firmware rejection.

## Watch out
A shared-gate timeout is covered by a classifier test only: the shared gate cannot be held for just the layout GET in the factory fakes. The provider merge of Blade14KeyboardEdition in WindowsVerifiedBladeDeviceProvider is near-equivalent, because the battery capabilities already carry the factory edition, so a provider test cannot discriminate it.
