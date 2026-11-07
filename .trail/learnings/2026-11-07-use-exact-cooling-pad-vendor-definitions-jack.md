---
title: Use exact cooling-pad vendor definitions for experimental control
author: Jack
date: 2026-11-07
branch: feat/cooling-pad-source-support
touches:
  - Directory.Build.props
  - src/OpenBlade.Contracts/IpcMessageCodec.cs
  - src/OpenBlade.Contracts/ServiceMessages.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.Animation.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.Lighting.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.cs
  - src/OpenBlade.Protocol/ProductionLightingPort.cs
  - src/OpenBlade.Protocol/Rz090528BrightnessLightingFactory.cs
  - src/OpenBlade.Service/ControlRequestHandler.cs
  - src/OpenBlade.Service/InteractiveSessionAuthority.cs
---

## Decided
Review the exact RC21-0230 / 1532:0F43 Synapse product3907 middleware before using community protocol recipes. Its pinned thermal and matrix definitions plus static firmware descriptors establish source-backed experimental control shared across the already-admitted Blade hosts. Preserve complete vendor fan ownership/preset/target and raw brightness, and save both persistent profile1 and volatile profile0 effect parameters. A fresh explicit read and matching ownership permit a new direct requested destination after a clean pause; baseline restoration is not a universal entry prerequisite.

## Tried and ruled out
Do not preserve the obsolete capture-only four-stage Medium/quiet/raw-mode UI as a product prerequisite. ReportID5 in the middleware mapping configuration describes input notifications; the updater firmware proves the unnumbered90-byte feature body and Windows91-byte reportID0 transport. Configured target0D81 is not measured speed, and generic0D88 naming alone does not establish exact-pad tachometer support. The updater includes an unrelated keyboard update_config.ini; use its exact device resources and firmware descriptor instead. Do not execute the updater or vendor binaries to inspect them.

## Watch out
Lighting GETs can return bounded variable payload lengths; status-only SET acknowledgements need independent field readback. Profile1 can still show Static while profile0 has a retained custom effect. The reviewed API cannot read another renderer's custom pixels, so do not overwrite an unrestorable buffer. Keyboard mirror publication must follow the complete successful frame transition, clear on fault/disposal by renderer owner, and remain a bounded latest snapshot. Source support does not prove physical fan speed or thermal performance, and no development-time hardware write is authorized by this review.
