---
title: Isolate typed query failures without weakening hardware admission
author: Jack
date: 2026-10-19
branch: fix/query-failure-isolation
touches:
  - Directory.Build.props
  - docs/developer-guide.md
  - src/OpenBlade.Protocol/AdapterPowerCommandClient.cs
  - src/OpenBlade.Protocol/BatteryCommandClient.cs
  - src/OpenBlade.Protocol/DeviceStateContentRejection.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.cs
  - src/OpenBlade.Protocol/NativeDeviceReadException.cs
  - src/OpenBlade.Protocol/ProductionBatteryPort.cs
  - src/OpenBlade.Protocol/RazerReport.cs
  - src/OpenBlade.Protocol/TemporaryFullChargeCommandClient.cs
---

## Decided

Isolate known failures only at closed typed state GET boundaries, across both validated battery/thermal backends and all experimental profiles. Preserve the actual protocol/native category, phase, query and native retirement evidence. Mark dependent battery or thermal fields unknown; publish independently confirmed results. Scoped control and full-transition readers must check every real dependency before writing.

Retire the failed port successfully and require successful native retirement before any new admission or partial publication. Managed FullyDisposed is not native retirement proof. Pending, faulted or canceled native retirement, failed disposal, identity/interface/controller admission, caller cancellation, exhausted/poisoned sessions, unclassified exceptions and uncertain writes remain fail-closed. Fresh admission and independent setter readback remain mandatory.

Keep optional GPU observation failures from discarding confirmed thermal state. Start the Tray service watch and shell without waiting for optional startup registration; run its synchronous scheduler read away from the UI thread and suppress publication after disposal. Existing independent Session Agent observers need no new wrapper.

## Tried and ruled out

The older content-only isolation rule unnecessarily discarded healthy controls after a status, checksum, correlation, length, native I/O or exchange-deadline failure. It is superseded at typed GET boundaries with structured exchange evidence and verified retirement. Do not broaden the semantic content-rejection classifier itself, accept failed reply bytes, isolate raw IOException/TimeoutException without exchange evidence, or reopen lighting factories as a supposedly read-only recovery shortcut.

## Watch out

Preserve native retirement through every result-to-exception conversion and keep partial exceptions out of whole-read retry loops. Full observation, scoped control, battery operations and transition consumers must all understand partial state. Performance/fans/HyperBoost are coupled; battery protection/temporary override are coupled. Pre-admission failures are not optional getter failures.

Failure-injection tests should cover startup, refresh, unrelated controls, cancellation, failed native retirement/disposal, fresh admission and no setter/replay. Startup-registration tests must wait for their intended state rather than raw dispatcher counts. GPU relinquishment uses the hardware-operation gate; queue it behind an injected read and release the read before awaiting it.
