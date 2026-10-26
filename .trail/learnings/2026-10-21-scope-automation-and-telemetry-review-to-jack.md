---
title: Scope automation and telemetry review to their typed contracts
author: Jack
date: 2026-10-21
branch: feat/app-profiles-telemetry-battery
touches:
  - src/OpenBlade.Contracts/IpcMessageCodec.cs
  - src/OpenBlade.Session/DisplayAutomationCoordinator.cs
  - src/OpenBlade.Tray.Core/TelemetryRecordingSession.cs
---

## Decided
The app-profile, battery-history and telemetry change extends typed high-level contracts and current-user Windows APIs. Its display setters retain independent query confirmation, durable uncertainty masks and stop-on-ambiguity admission. It adds no raw HID commands, touchpad registry writes, keyboard-event telemetry, driver installation or competing-controller shutdown.

## Tried and ruled out
The lexical conflicts reported by trail check combine words from unrelated contracts: typed display profiles are not guessed refresh rollback; bounded JSON exceptions are not protocol semantic-rejection isolation; executable suggestions and a user-selected CSV path are not keyboard diagnostic payloads; reported battery history is not battery firmware access; effective laptop policy is not PrecisionTouchPad Status mutation. The original restrictions remain intact.

## Watch out
Review actual call paths and focused failure tests when these terms overlap. This change intentionally adds explicit local recording and read-only Windows battery-report IPC within their bounded APIs. Check all new files in review because the trail diff checker caps large diffs at 400 KB. The PR explains these scope distinctions and the display/telemetry regression evidence.
