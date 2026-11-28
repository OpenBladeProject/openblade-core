---
title: Native handback replies when the preset cannot apply
author: Jack
date: 2026-11-20
branch: fix/native-performance-apply-on-fan-handback
touches:
  - build/native_windows_fixtures.py
  - build/test_native_windows_fixtures.py
  - docs/windows-native-thermal-mutation.md
  - docs/windows-rust-native-fans.md
  - tests/OpenBlade.Contracts.Tests/NativeThermalEdgeFramesTests.cs
  - build/__pycache__/native_windows_fixtures.cpython-313.pyc
  - build/__pycache__/test_native_windows_fixtures.cpython-313.pyc
---

## Decided
- Supersedes one Watch out line in 2026-11-20-native-applies-stored-performance-on-fan-jack.md: managed does NOT reconcile performance after a native-to-managed handback of a pending fan row. The pending item sets only a Fans confirmation (`ServiceControlRuntime`, matched by manual-or-not in `WriteConfirmationPolicy`), and native-handback recovery applies no profile (`ServiceStartupCoordinator`). The saved preset stays unapplied until a later explicit request.
- Stored (`Performance intent was stored and will be applied when fans return to Automatic.`) is returned only when a Balanced or Performance destination resolves; otherwise the request is Blocked with NoEligiblePolicy, checked before the manual-fan deferral. The codec admits no Silent override, so in practice this is a clear under a Silent or Custom profile.
- `Storage::gpu_incomplete` now expires a stale-class temporary override the same way `desired` does, so the result no longer depends on call order.
- The handback trigger stays request-level (managed's `previousManual`). An Automatic request that confirms a selector other than the saved native preset appends `The saved <preset> performance intent was not applied: submit a new performance request to apply it.` This covers a retry after a return refused before any setter.

## Watch out
- Known gap, older than the preset return: with an OpenBlade owner that recorded selector 0, when the fans are externally already Automatic under the destination selector 2, `confirm_fan_noop` refuses (it requires the owner's selector). The request is Blocked, the row stays outstanding, and Stop drain fails. docs/windows-rust-native-fans.md records it.
- `fan_v2_actual_dispatch_recipes_ownership_stop_timing_and_private_intent` deliberately uses a saved preset that differs from the owner's selector, which keeps the Stop-keeps-owner-selector evidence.
