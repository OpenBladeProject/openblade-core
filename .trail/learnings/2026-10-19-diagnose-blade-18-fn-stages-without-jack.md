---
title: Diagnose Blade 18 Fn stages without changing input authority
author: Jack
date: 2026-10-19
branch: fix/739-offline-controls
touches:
  - CHANGELOG.md
  - docs/blade18-fn-decoder.md
  - docs/blade18-observer-evidence.md
  - src/OpenBlade.Protocol/ExperimentalBladePort.Custom.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.Fans.cs
  - src/OpenBlade.SessionAgent/Program.cs
  - src/OpenBlade.SessionAgent/Rz090484FnInputState.cs
  - src/OpenBlade.SessionAgent/ServiceReactiveLightingLoop.cs
  - src/OpenBlade.SessionAgent/WindowsReactiveKeyboardSource.cs
  - tests/OpenBlade.Protocol.Tests/ExperimentalThermalExpansionTests.cs
---

## Decided
Blade 18 Fn diagnosis uses bounded stages in the existing Session Agent log, appended by About > Export diagnostics. Emit first admitted Fn state and transitions, known P/R/T hook and Raw Input confirmation, aggregate queue reset/supersession, and executor outcome. Use closed stage/shortcut/phase values and exception type only. A throwing diagnostic sink cannot affect state updates or action routing.
## Tried and ruled out
No raw keys, HID bytes, handles, user paths, exception messages, new observer, IPC or telemetry loop. Tests invoke production emission seams rather than helper constructors. Do not loosen exact identity, local session or exclusive keyboard topology merely because shortcuts were reported inactive.
## Watch out
ExecutorCompleted means the call returned, not physical hardware success. PendingActionsReset can clear work without asserting an item existed; queued R/T items do not have per-item terminal records. A single ordinary export with settings unchanged is the next concrete request; avoid another capture/test round. Async view-model tests must wait for bound values, because test dispatchers can expose pending flags before values update; hosted CI caught this race in the one-shot recovery test.
