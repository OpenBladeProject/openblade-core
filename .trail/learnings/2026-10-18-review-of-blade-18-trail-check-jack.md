---
title: Review of Blade 18 trail check keyword conflicts
author: Jack
date: 2026-10-18
branch: feat/blade18-observer-support
touches:
  - CHANGELOG.md
  - docs/blade18-spatial-lighting.md
  - docs/experimental-blade-reference.md
  - docs/experimental-blade-support.md
  - docs/keyboard-lighting-file-format.md
  - docs/ru-RU/experimental-blade-support.md
  - docs/zh-CN/experimental-blade-support.md
  - src/OpenBlade.Contracts/DeviceState.cs
  - src/OpenBlade.Contracts/IpcMessageCodec.cs
  - src/OpenBlade.Contracts/KeyboardLightingSequence.cs
---

## Decided
The trail check against the earlier Blade 18 recovery note produced keyword-based false positives. Exact model admission remains required. Product 672 host Gaming Mode and correlated default Fn shortcuts remain admitted; primary-mode inversion remains unadmitted. The lighting engine device manifest and middleware installer manifest are different sources with different purposes. Blade 18 uses a 96-address frame; unrelated fixture numbers and line references are not frame-geometry changes. Mentioning this laptop in support docs does not enable THX or SnapTap. These changes preserve those decisions.

## Tried and ruled out
Reviewed all twenty reported conflicts from the 2026-10-18 check; none identified a code path enabling function-primary inversion, an unknown hardware command, another model's ABI, or a generic SDK admission.

## Watch out
Use semantic source and focused tests to assess these matches. Do not remove exact admission or supported default Fn shortcuts to satisfy a keyword-only checker.
