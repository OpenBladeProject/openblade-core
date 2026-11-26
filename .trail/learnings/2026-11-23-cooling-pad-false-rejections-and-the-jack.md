---
title: Cooling pad false rejections and the outside-change policy
author: Jack
date: 2026-11-23
branch: fix/cooling-pad-false-rejection
cost: { minutes: 35, tokens: 396902 }
touches:
  - Directory.Build.props
  - docs/cooling-pad.md
  - src/OpenBlade.Service/CoolingPadControlRuntime.cs
  - src/OpenBlade.Service/ServiceControlRuntime.CoolingPad.cs
  - src/OpenBlade.Tray.Core/CoolingPadViewModel.cs
  - src/OpenBlade.Tray.Core/Properties/Resources.qps-ploc.resx
  - src/OpenBlade.Tray.Core/Properties/Resources.resx
  - src/OpenBlade.Tray.Core/Properties/Resources.ru-RU.resx
  - src/OpenBlade.Tray.Core/Properties/Resources.zh-CN.resx
  - tests/OpenBlade.Service.Tests/CoolingPadControlRuntimeTests.cs
---

## Decided
- The false "did not accept the change" came from ApplyCoreAsync saving settings, resume intent and status.Requested before its final Allowed() check. It returned Blocked, then MaintainAsync (Requested drives fan targets) or ResumeSavedControlAsync applied the saved request anyway. The final check now runs before anything is saved.
- A native LocalAdmissionDeferred exception before any completed write in an apply is reported as Blocked, so the tray can retry it.
- The tray retries a Blocked apply up to MaxRefusedRetries (5) every 3 s and keeps the draft (changePending). Automatic reads now clear uncertainSubmission once a read confirms the pad. The uncertain write is never resent.
- The pad follows preferences.ThermalControlPolicy directly, without the laptop thermal ledger. Restore them reapplies after an outside change (3 per 10 min) and resumes after handbacks. Notify me, or a fourth change, releases control: it restores only the parts that were not changed outside, clears the journal and the resume intent.
## Tried and ruled out
- Using SetThermalControlPolicyAsync in a lifecycle test: it is Blocked unless the thermal ledger is authoritative. Seed BatteryConfigurationStore plus ThermalTestUserConfigurationRepository instead.
- Projecting the outside-change read into status: it overwrites ConfirmedTargetRpm with the ramping speed and causes extra demand writes.
## Watch out
- Detection must run before a maintenance fan write. SetFanAsync(Auto/HostCurve) silently reselects the preset, which hides an outside change.
- Fixed-mode lease writes change the fan record, so `controlled.FanConfiguration` is re-read after them.
- ReadFan reads only the running owner's record, so a change under the other owner record is undetectable.
- On this host the native build needs rustc on PATH plus a cargo shim that strips `+1.99.1`, because rustup is missing. Use a subst drive for Tray.Tests.
