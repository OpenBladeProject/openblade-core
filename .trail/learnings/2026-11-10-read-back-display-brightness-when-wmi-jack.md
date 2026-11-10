---
title: Read back display brightness when WMI omits the return status
author: Jack
date: 2026-11-10
branch: fix/display-brightness-no-wmi-status
touches:
  - Directory.Build.props
  - src/OpenBlade.Display/WindowsDisplayBrightness.cs
  - tests/OpenBlade.Display.Tests/WindowsDisplayBrightnessTests.cs
---

## Decided
Issue #941 diagnostics from 0.58.4 show Windows returned a null WmiSetBrightness status after invocation. Let the shared acknowledgement validator continue for null, as for Boolean. Both the settings/profile controller and the WMI hotkey fallback independently verify the requested level on the written WMI instance.

## Tried and ruled out
PR #939 already handled Boolean replies. Four missing-status regression cases reproduced the same exception on the 0.58.4 code. Missing status alone cannot establish success. A different instance, wrong level, or unavailable readback remains a failure.

## Watch out
Keep documented nonzero UInt32 errors and unexpected response types as errors. Do not retry a write based on missing status or readback. Software verification did not exercise the reporter's panel.
