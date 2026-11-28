---
title: Separate thermal retirement, durable settlement and current policy
author: Jack
date: 2026-11-13
branch: fix/native-thermal-edge-corrections
touches:
  - tools/openblade-windows-host/src/windows/scm/installed/mailbox_read.rs
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_storage.rs
  - tools/openblade-windows-host/src/windows/scm/installed/durable_owner.rs
  - tools/openblade-windows-host/src/service_status.rs
---

## Decided
Record the pre-activation reader-drain phase before awaiting retirement. Cancellation or suspend can reconstruct a lazy reader only after the original callback actually retires and the original owner/root integrity still verifies. A failed Prepared-to-KnownNotSubmitted append is attempted once and retains its permit, quarantine and bytes; it does not turn positive callback retirement into uncertain native I/O. Fresh reads can recover while writes remain fenced. Shutdown can publish actual custody retirement after a specifically typed local settlement error, then return that error. Fatal admission, owner/root protection loss, poison, uncertain activation and incomplete callback drain retain their fences.

Recompute retained private policy at the publication boundary after rebasing on current public metadata. Temporary GPU intent saved before a Battery refresh must not reappear in the later Stored event. Private configuration drift preserves the actual public read witness and its original deadline; its diagnostic identifies private policy unavailability. Typed fatal capture/health errors propagate through GetStatus, ObserveState and the outer driver.

Application fan precedence is explicit Fans, FanControl.Mode, then the base profile, beneath persistent intent. Exercise it through accepted ReportApplicationContext and its real lease. Persistent FanControl without Fans remains invalid. Stored manual-fan results require a new explicit performance request after selecting Automatic; observation does not apply saved performance automatically.

Complete schema-8 saved Custom intent is distinct from observed CustomManual levels, which remain unknown. The real managed GPU serializer also emits effectiveOffsets. Validate that optional computed property against enabled/offsets with the same bounded parser for saved intent and typed requests, preserving the whole compatible intent. This grants neither a Custom setter nor a GPU setter.

## Tried and ruled out
Fixture-only JSON omitted effectiveOffsets and hid a real managed/Rust compatibility defect. Generate fixtures through the actual managed codec/store and decode actual native response/event bytes through strict IPC80. Keep original IPC79 files as rejection controls and preserve legacy51 compatibility. A same-value observation intentionally coalesces; a subscription regression must create a material public change before expecting another event.

## Watch out
All current evidence is offline compilation, explicitly audited injected bodies, and supplied own-SID private file/ACL/sharing fixtures. Actual Windows process/session/controller/HID/sensor suppliers are compile-only. The finite transaction budget, 1 MiB journal growth limit, complete managed handback consumer, complete capabilities, Agent/helpers, model breadth and automation/cutover remain separate work. Passing these tests is not installed or physical validation.
