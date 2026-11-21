---
title: Unreadable 07/8F must not block the experimental limiter
author: Jack
date: 2026-11-21
touches:
  - CHANGELOG.md
  - docs/experimental-blade-support.md
  - docs/ru-RU/experimental-blade-support.md
  - docs/zh-CN/experimental-blade-support.md
  - evidence/source-reviewed-blades/2026-11-19-temporary-full-charge-rz09-0529-0530.json
  - src/OpenBlade.Contracts/DeviceState.cs
  - src/OpenBlade.Protocol/ExperimentalBladeModels.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.Battery.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.TemporaryFullCharge.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.cs
---

## Decided
- Experimental backends (0x02A0, 0x02C5, 0x02C7) carry DeviceCapabilities.ChargePolicyIndependentOfTemporaryFullCharge (JsonIgnore, service-side only). Their 07/92 limiter never depended on 07/8F, so an unknown override admits limiter plans and PolicyOnly confirmation; only a confirmed active override blocks the limiter. Production backends keep refusing an unknown override.
- A failed or undecodable 07/8F leaves TemporaryFullCharge null (never false) and SupportsTemporaryFullCharge true, so override enable and cancel stay Blocked.
- ExperimentalFeaturesPolicy.TemporaryFullChargeRecoveryRequired is set only by a possibly submitted 07/0F write. Fan, performance, lighting and limiter pauses recover without 07/8F; the optional read runs last and is skipped while TemporaryFullChargeReadUnavailable.
- Temporary full charge enable admits ExternalPowerReported (Windows AC = ExternalUnknown). Battery refuses; cancel needs no power.
## Tried and ruled out
- Passing the state-read failure into the failures list for independent backends: it turns a refused 07/8F into a partial read and regresses 0x02C5/0x02C7, which never read 07/8F before.
## Watch out
- Status 5 or a timeout on 07/8F ends the session. The first limiter write after the flag becomes readable again can fail once as KnownNotApplied, and one recovery attempt can fail; the retry skips the read.
- The tray never sees the JsonIgnore flag, so the tray lets the limiter through on an unknown override and relies on the service refusal for production backends.
