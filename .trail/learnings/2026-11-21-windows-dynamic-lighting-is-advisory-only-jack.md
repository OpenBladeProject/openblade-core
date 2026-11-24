---
title: Windows Dynamic Lighting is advisory only (correction)
author: Jack
date: 2026-11-21
branch: feat/dynamic-lighting-ownership
---

Correction to 2026-11-21-windows-dynamic-lighting-ownership-beyond-rz09-jack.md: that design was refuted in review and reworked in PR #992.

## Decided
- Windows Dynamic Lighting is advisory only. The Session Agent reports `WindowsDynamicLightingAdvisory` (Unknown/Off/MayControl) over `ReportWindowsDynamicLightingAdvisoryRequest` (protocol 87). Nothing in the service gates, retires, restores or clears lighting because of it, and the saved `KeyboardLightingControlMode` is untouched.
- MayControl means the global `AmbientLightingEnabled` switch is on or not configured, and exactly one connected exact-PID LampArray is present. It is not ownership.
- The service binds each report to the reporting user SID and that session's still-controls predicate. A user switch drops the report; a lost session, an unverified device or a different model publishes Unknown.
- Only an active-session agent observes, and it reports only when its advisory differs from the published one. A service restart or user switch publishes Unknown, which triggers a resend; no heartbeat is needed.
- `WindowsDynamicLightingLampArrays` admits the advisory only. `LampArrayLightingAccess`, and with it the delegation toggle, stays RZ09-0528-only, exactly as on main.

## Tried and ruled out
- `LampArray.IsEnabled` as an ownership signal. WinRT documents it as the calling app's own update gate, and it defaults to true. The RZ09-0528 capture recorded `enabled: true` while the array was unavailable. Classifying on it blocked every admitted laptop by default.
- Retiring the renderer on Owned. On RZ09-0528 the ordinary close restores Normal mode, so 30 s reports flapped Driver mode. The experimental write-free close left the ownership state behind for the next port.

## Watch out
- No observed signal for real Windows ownership exists yet. Before any blocking, take a read-only physical observation on an admitted laptop of the global switch, the per-device toggle under `HKCU\Software\Microsoft\Lighting\Devices`, and `IsAvailable`.
- `NotifyComputed` deduplicates by value, so a test that expects a notification for an unchanged computed value fails for the right reason.
- In PowerShell 5.1, `’` closes a single-quoted string, and `Get-Content -Raw` reads UTF-8 without a BOM as ANSI. Edit such text with the Edit tool or `[IO.File]`.
