---
title: Native power-setting notifications feed the low-power gate
author: Jack
date: 2026-11-22
branch: feat/native-power-setting-notifications
touches:
  - docs/windows-native-thermal-mutation.md
  - rust/Check-Workspace.ps1
  - rust/crates/openblade-domain/src/lib.rs
  - tests/OpenBlade.Service.Tests/OpenBlade.Service.Tests.csproj
  - tools/openblade-windows-host/src/origin_lifetime.rs
  - tools/openblade-windows-host/src/scm_host.rs
  - tools/openblade-windows-host/src/windows/scm.rs
  - tools/openblade-windows-host/src/windows/scm/installed.rs
  - tools/openblade-windows-host/src/windows/scm/installed/native.rs
  - tools/openblade-windows-host/src/windows/scm/lifecycle.rs
---

## Decided
- The native Service registers GUID_ACDC_POWER_SOURCE and GUID_CONSOLE_DISPLAY_STATE against its SCM status handle with DEVICE_NOTIFY_SERVICE_HANDLE, not managed's DEVICE_NOTIFY_CALLBACK. Changes arrive at the one SCM handler as POWEREVENT/PBT_POWERSETTINGCHANGE with the same POWERBROADCAST_SETTING payload, ordered with every other notice. Suspend/resume needs no registration; SERVICE_ACCEPT_POWEREVENT delivers it. Only the thermal composition registers, because only it accepts power events.
- Registration failure stops the Service as failed (the managed constructor throws). Unregistration runs inside the report closure when the status is SERVICE_STOPPED, before SetServiceStatus, because SCM may end the process after it. Order, attempt-both and keep-first-outcome follow WindowsLifecycleRegistrationCleanup.
- The decoder, classifier and notice-to-signal mapping live in `openblade_domain::power_setting`, pinned by `power-setting-vectors.json`. The recorder calls managed's private `TryReadPowerSettingDword` by reflection over an unmanaged copy of each row. Native dispatches on the GUID because one handler receives both settings.
- The display state lives in `LifecycleState`, under the same lock as the epoch and gate.
- The unwired lifecycle worker (`windows/scm/lifecycle.rs` Worker/Callbacks/Activity) was retired, not wired: nothing consumed it, and wiring it would have made a second low-power state beside the gate. The file keeps only `observe()`/`Observation`/`PowerSource`, which `installed/status_source.rs` includes by `#[path]`. Its dev metadata fixture in `wire.rs` and `metadata_lifecycle.rs` went with it.

## Tried and ruled out
- Making the shim's copy bound observable through `decode` alone. `decode` rechecks DataLength, so reading the whole 24-byte scratch (mutant S05) is output-equivalent. The bound is pinned by testing `payload_len` directly.

## Watch out
- Display dim, off and on and AC/DC notices now reach the Service, and each one advances the epoch. In-flight captures fence more often than before B2. That is intended, but it shows up as more retried captures.
- `installed_ingress` and `Lifecycle::ingress` are now `#[cfg(test)]`. The installed hook uses `installed_notice`/`notice`. The readiness mutation tests still call `installed_ingress`.
- In this worktree the Service.Tests csproj is LF in the working tree, despite autocrlf. Match the file and do not convert it.
- Live GUID delivery, Modern Standby giving no PBT_APMSUSPEND, and resume recovery (S3 and Modern Standby, with a missed resume) are untested on hardware.
