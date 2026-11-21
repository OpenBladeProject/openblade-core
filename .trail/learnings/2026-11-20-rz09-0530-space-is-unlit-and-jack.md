---
title: RZ09-0530 Space is unlit and keyboard-layout reads are cached per device identity
author: Jack
date: 2026-11-20
branch: feat/rz09-0530-regional-keymaps
touches:
  - CHANGELOG.md
  - docs/blade14-keymap.md
  - docs/experimental-blade-support.md
  - docs/ru-RU/experimental-blade-support.md
  - docs/zh-CN/experimental-blade-support.md
  - evidence/source-reviewed-blades/2026-11-12-pi4110-keymap.json
  - evidence/source-reviewed-blades/2026-11-13-complete-lighting-pass.json
  - src/OpenBlade.Contracts/Blade14KeyboardLightingLayout.cs
  - src/OpenBlade.Protocol/ExperimentalBladeFactory.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.KeyboardLayout.cs
---

## Decided
On the PI4110 image, Space (usage 0x2C) has a record of 2C followed by seven zero bytes in all three descriptor tables (0xB293, 0xB5CB, 0xB8EB; slots 86/87/86), and renderer 0x682C skips usage 0x2C at 0x685E. Blade14KeyboardLightingLayout drops it from lit, addressable, pulse and import bindings. The lit union is 85 slots; per map AFE3 78/75, B313 79/76, B63B 82/79 (lit slots / scan bindings). Common stays 74/71.

The optional 00/86 keyboard-layout read (RZ09-0530 edition and RZ09-0484 layout) is now settled once per factory for the admitted host evidence plus exact HidHandleIdentity (including VersionNumber), via ExperimentalKeyboardLayoutMetadata. A confirmed value and a completed failure are both kept; cancellation is not. Factory.Capabilities carries the settled value, so write-pause recovery publishes it.

## Tried and ruled out
Reading the edition on every port open re-poisoned battery and lighting sessions after a firmware rejection and lost the value in recovery, which publishes factory capabilities.

## Watch out
The Space renderer skip came from an independent review; tools/firmware/verify_blade14_keymap.py now asserts it, but that verifier has not been rerun here because no pinned image or python is available locally. A test fake Session that reports Healthy after a timeout does not model production, which retires that session.
