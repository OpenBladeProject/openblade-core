# RZ09-0528 support runbook

This is the current handoff for Razer Blade 16 `RZ09-0528`, BIOS `2.02`,
USB `1532:02C6`. It describes what OpenBlade may expose, what remains gated,
and how to advance support safely.

The chronological, append-only record was preserved in the
[archived implementation log](rz09-0528-support-implementation-log-archive.md).
Do not append new status updates there.

The authoritative feature status is the sanitized
[device-coverage ledger](https://github.com/OpenBladeProject/openblade-captures/blob/main/decoded/rz09-0528-pid-02c6-bios-2.02-device-coverage.json).
If this summary and the ledger disagree, the ledger wins and this summary must
be corrected.

## Exact-device boundary

- Model: `RZ09-0528`
- Validated BIOS baseline: `2.02`
- USB VID/PID: `1532:02C6`
- Control collection: usage `0001:0002`, 91-byte feature reports
- Windows firmware metadata observed during admission: EC `1.09`, MCU
  `1.9.0.0`

Never reuse assumptions from `RZ09-0581` / PID `02E0`. Shared command shapes
are not evidence of shared ranges, arguments, matrix geometry, or input
mappings.

## Status meanings

The coverage ledger uses these states:

| State | Meaning |
| --- | --- |
| `ProductionAdmitted` | Exact-device evidence, guarded implementation, tests, and proportionate validation are complete. |
| `SetterValidated` | A reversible exact-device write was validated but is not yet a production capability. |
| `QueryValidated` | Independent readback is validated; mutation is not admitted. |
| `Captured` | Sanitized evidence exists, but production admission is incomplete. |
| `SoftwareOnly` | The behavior is implemented and tested without claiming a firmware capability. |
| `Absent` | The exact model or visible vendor UI does not expose the capability in that context. |
| `NotInvestigated` | No conclusion is allowed. |

Do not infer GET support from visual confirmation, successful SET
acknowledgements, or host-rendered lighting. Do not convert `Captured` into
production support merely because a neighboring model implements it.

## Current capability surface

| Area | Current scope |
| --- | --- |
| Identity | Exact model, BIOS, PID, control collection, firmware metadata, and relevant interfaces |
| Keyboard lighting | Off, brightness query/range, and all visible Synapse effect bases: Ambient Awareness, Audio Meter, Breathing, Fire, Reactive, Ripple, Spectrum Cycling, Starlight, Static, Tidal, Wave, and Wheel |
| Logo lighting | Off, Static, Breathing, and getter |
| Battery | Protection off, 50–80% limits in 5% steps, limit query, and temporary-full-charge query/enable/cancel |
| AC performance | Balanced, Silent, Performance, Custom, exact admitted CPU/GPU combinations, and readback |
| GPU tuning | Enable/disable and core/memory offsets through NVIDIA NVML, with independent NVAPI validation on the exact model |
| HyperBoost | Direct entry from Silent, Balanced, Performance or Custom with the exact cooling pad or, on the full-power adapter, without it; paired mode-7 readback and restoration to the starting mode, including Custom CPU/GPU levels |
| Fans and sensors | Automatic, fixed `2000–5000 RPM`, Maximum, host-rendered manual curves, fan RPM query, and exact Windows CPU thermal-zone reading |
| Keyboard behavior | Function-key primary mode, Gaming mode, startup animation, Normal/Driver device mode, and their admitted getters |
| Special keys | Exact PID `02C6` M3 Gaming, M4 Performance, M5 microphone-mute mappings, and Fn+P performance cycling |
| Power source | Full-power barrel AC, USB-C power classes, battery classification, and admitted transition/readback leaves recorded by the ledger |
| Display | Refresh modes, Dynamic Refresh Rate, Variable Refresh Rate range, HDR, and SDR color profiles through Windows APIs |
| Ownership | Windows Dynamic Lighting delegation, zero-write delegation policy, external-change detection, and explicit reclaim behavior at their ledger statuses |

Effect-specific parameter variants retain their individual ledger statuses.
There is no admitted native effect or matrix getter. Host-rendered effects must
not be described as independently read back.

Manual fan curves persist OpenBlade intent and render it through the validated
fixed-speed primitive. That software intent is distinct from physical fan RPM
readback.

## Remaining frontier

Use the coverage ledger for the complete leaf-level list. The main unresolved
groups are:

- USB-C and battery performance setters and remaining transition/lifecycle
  validation;
- CPU Power Limit and CPU Voltage Optimizer ownership without
  a Synapse-delivered proprietary kernel-driver dependency;
- effect parameter combinations, native effect/matrix readback, and remaining
  spatial key-map coverage;
- M1 and M2 discrimination and suppression; Fn+P is admitted through the
  captured composite Fn-state and P input while M3–M5 retain their mappings;
- cooling-pad HyperBoost sustained thermal and remaining lifecycle validation;
- cold boot, sign-in, sleep/resume, and device reconnect.

CPU Power Limit and CPU Voltage Optimizer remain discoveries,
not writable OpenBlade controls. Preserve them in the capability model and
documentation even when OpenBlade cannot safely own their backend. Never
silently drop a newly discovered model feature.

### HyperBoost developer validation

The exact `02C6` vendor capture and independent paired readbacks establish
mode 7 and restoration to Performance/Automatic. They do not establish
padless safety or cooling-pad fan control. The baseline capture contained no
thermal query, so every validation must freshly read Performance/Automatic
before applying the captured pair. After the vendor capture, the present
operator restored High through the vendor UI and confirmed the pad fan kept
running after vendor software exited. This is attended readiness evidence,
not independent pad-control or fan-RPM readback. Every run still requires a
fresh operator confirmation that the genuine pad fan is ready.

The developer-only command is:

```text
OpenBlade.Capture validate-rz09-0528-02c6-hyperboost-performance --confirm-target RZ09-0528:1532:02C6:BIOS-2.02:HYPERBOOST:RESTORE-PERFORMANCE --confirm-pad-ready ATTENDED:COOLING-PAD-FAN-READY
```

It requires the exact captured host and firmware, one control collection,
OpenBlade service and competing controllers stopped, no USBPcap capture,
fresh exact-pad presence, and full-power adapter readback before each write.
It confirms both channels and restores only the freshly saved Performance
baseline. Ambiguous submission or readback stops further writes and reports
manual recovery; it never retries blindly. This short round trip does not
qualify sustained load, padless operation, adapter removal, or sleep/resume.

The service and tray use the capability-gated pad-required path validated by
the exact-device developer round trip on 2026-09-27. Both enable channels were
acknowledged and independently read as mode 7, then both channels were restored
and independently read as Performance/Automatic. The embedded exact-model
fixture is checked against its reviewed SHA-256 before command admission.
The [sanitized developer checkpoint](https://github.com/OpenBladeProject/openblade-captures/blob/34b5da28b7bbf9391eed9298676ee235e0bc76b3/decoded/rz09-0528-pid-02c6-bios-2.02-hyperboost-developer-validation.json)
records the exact build, acknowledgements, independent readbacks and restoration.
The path requires an explicitly writable Performance base mode,
keeps the cooling-pad dependency in the ownership journal, and preserves the
entry-time performance and fan intent. Confirmed pad absence during the existing
observation cycle permits one owned exit; uncertain enumeration does not prove
disconnection. The non-writable Silent/Balanced access entries are contract
placeholders and do not establish captured direct-transition evidence. The
2026 model retains its separately admitted padless Balanced transition.
GPU tuning was independently validated in Balanced, Silent, Performance and
Custom modes. A separate exact-device combined validation on 2026-09-27 passed
both sequences recorded in the [combined validation checkpoint](https://github.com/OpenBladeProject/openblade-captures/blob/16a350740ac971ce7b56bc9c9233ce060c8951ed/decoded/rz09-0528-pid-02c6-bios-2.02-hyperboost-gpu-developer-validation.json): enter HyperBoost, apply +5/+5 logical MHz, restore zero, then
exit; and apply +5/+5 before entry, confirm the offsets through entry and exit,
then restore zero. NVML and NVAPI agreed at each stage. A separate read-only
process confirmed zero offsets and a typed query confirmed Performance/Automatic
before the original settings and controller services were restored. These
bounded checks do not establish sustained-load stability or padless operation.

Installed build `0.23.0+6e10043f` passed the typed service enable/disable
round trip and a separate pad-disconnect check. The latter confirmed a
service-owned exit with zero helper disable calls, journal removal and original
settings restoration. The [installed evidence](https://github.com/OpenBladeProject/openblade-captures/pull/24)
records both sessions and their exact build provenance.

Separate developer experiments with the pad USB and power disconnected passed
an immediate Performance-to-HyperBoost round trip and a 60-second no-load
observation. All 30 observation samples retained mode 7; the EC-backed thermal
zone reported 45 C and the GPU remained explicitly inactive. Fans rose from zero to
2900/2700 RPM. This zone is not a verified CPU core sensor. The frozen 55 C
experimental abort threshold guarded that zone, not a CPU core thermal limit.
Both experiments restored Performance, then the original
Balanced settings, and independently confirmed zero GPU offsets. The
[sanitized no-load report](https://github.com/OpenBladeProject/openblade-captures/blob/c12bacf4ebbc66a098fb1cef7ab901dd02bd4398/annotations/2026-09-27-rz09-0528-padless-hyperboost-no-load.json)
records actual timestamps and readings. These results alone did not admit
installed padless operation; see [Pad-less production admission](#pad-less-production-admission).

The developer-only `validate-rz09-0528-02c6-hyperboost-padless-loaded` command
prepares a separate ten-second observation under an exact Steel Nomad workload.
It requires the full-power adapter, disconnected and unpowered cooling pad,
zero GPU offsets, and a fresh timestamped JSON log from an already installed,
verified HWiNFO process. It selects the named CPU (Tctl/Tdie) sensor and retains
temperature peaks between reads; GPU temperature and utilization come from the
same verified NVML device. HWiNFO is a validation tool, not an installed
OpenBlade dependency.

The reviewed plan uses experimental entry limits of 70 C CPU and 72 C GPU,
and stops observation at 75 C on either sensor. These values are not hardware
ratings. The ten-second observation deadline excludes paired transition and
restoration latency, which are recorded separately. A telemetry failure ends
observation and permits restoration only while the existing native guards
confirm a known state. Ambiguous firmware operations stop further writes.
The surrounding validation restores original settings after the benchmark
exits. This command provides no production padless admission; its hardware
results require separate review.

Developer lifecycle validation selects a separate exact-model contract. The
RZ09-0528 readiness descriptor uses Performance/Automatic and binds each
non-lifecycle gate to merged, hash-pinned 2025 evidence: the immediate run,
the 60-second no-load and Stress Test plans and runs, the post-restoration
pad-absence record, and the battery and USB-C boundary runs. Its low-power and
crash-recovery gates stay closed by design, because only installed lifecycle
runs can satisfy them; the RZ09-0581 admission manifest cannot authorize this model.
Service requests bind the selected manifest as well as the exact build, and
only a separately built developer lifecycle-validation service can use this
readiness. The FirmwareSmoke harness selects the Performance transition for
RZ09-0528 and admits only a Performance/Automatic entry without GPU tuning;
the service independently refuses any other entry. The harness does not yet
check GPU offsets or the NVIDIA driver version before an installed run.

The developer-only battery-boundary command observes a separate attended
full-power AC to battery to AC transition without a workload. It requires
fresh CPU/GPU telemetry, zero GPU offsets, low GPU activity, pad absence and
disconnected USB-C power. Firmware setters remain restricted to confirmed
full-power AC. Battery mode 3 is an observed state, not a newly admitted setter.
On return, two matching full-power observations may confirm mode 2 without a
write or permit the existing guarded mode-7-to-2 restoration. A well-formed
sample whose power or paired mode changes during one read bracket is treated
as a plug-event transition: it is discarded, resets the full-power stability
window, and is retried within the same deadline. Malformed readings,
unsupported stable modes, mixed pairs that persist to the deadline, or
uncertain native operations leave recovery pending. A separate USB-C variant
uses the same entry, setters and cleanup, but its destination is the
read-only-confirmed 100 W USB-C value `0711` with Windows AC online. The
operator connects USB-C before HyperBoost is applied, removes only the
barrel, and reconnects it when `0711` is observed. An unexpected battery
reading in that variant aborts to the same full-AC cleanup, while USB-C
readings poison the battery variant. Windows reports AC online for both full
AC and USB-C, so only the firmware reading distinguishes them. Both variants
were later hardware-validated. Neither validates installed service lifecycle
recovery.

### Pad-less production admission

Installed lifecycle runs on build `0.23.0-nightly.4+8ed9088a` closed the last
two gates. An unexpected Service termination and one Windows Sleep each ended
with exactly one owned restoration to Performance/Automatic, matching desired
state, no external changes and a retired journal
([crash recovery](https://github.com/OpenBladeProject/openblade-captures/pull/31),
[low power](https://github.com/OpenBladeProject/openblade-captures/pull/32)).
The exact-model admission manifest
`Rz09_0528_02c6_2_02_HyperBoostWithoutCoolingPadAdmission.json` binds all ten
gates to hash-pinned 2025 evidence. It is model-scoped: BIOS 2.02 is capture
provenance, not a firmware restriction.

Typed round trips on the exact laptop confirmed mode 7 and exact restoration
from Silent (mode 5) and Balanced (mode 0) as well as Performance (mode 2), so
HyperBoost enters directly from the current one of these modes and restores it
on exit, with or without the pad. Pad-less entry uses the full-power adapter
(`FullPower280W`, value `1111`), a fresh exact pad-absence observation and
competing-controller checks, and never switches the performance mode first.
Custom (mode 4) enters the same way; exit writes the Custom pair and the
service re-applies the saved CPU/GPU levels, confirmed on the installed build
with CPU Medium and GPU High on both pad paths. A separate pad-less round trip
confirmed +5/+5 MHz GPU offsets applied during HyperBoost and held through entry
and exit, with both NVML and NVAPI returning to zero, so GPU tuning stays
available with or without the pad. A fresh
pad observation selects and journals the path at entry; the port submits only
under that path and rechecks it before every command, so a pad change
mid-transition blocks or becomes indeterminate instead of switching paths.
Only a cooling-pad session exits automatically when the pad disconnects.

GPU tuning is separately admitted after exact-model apply/readback/restore
validation in Balanced, Silent, Performance, and the tested Custom combination.
The NVIDIA driver supplies the writable offset backend; no Synapse-delivered
CPU tuning driver is used. Core offsets remain bounded to `-1000–400 MHz` and
memory offsets to `-1000–3000 MHz`, further restricted by the driver's reported
range. The successful bounded validation does not certify sustained stability
at every offset. CPU tuning and untested Custom combinations are not enabled
by this GPU admission.

## Production map

Key implementation boundaries in `openblade-core`:

- `src/OpenBlade.Protocol/Rz090528BrightnessLightingFactory.cs` contains
  exact-device keyboard and logo lighting behavior.
- `src/OpenBlade.Protocol/BatteryValidationFactory.cs` composes the
  exact-device battery port, and `src/OpenBlade.Protocol/ProductionBatteryPort.cs`
  contains the battery, performance, fan, adapter, sensor, and firmware
  capability surface.
- `src/OpenBlade.Service/ServiceComposition.cs` selects exact-device factories
  only after the verified-host gate.
- `src/OpenBlade.Service/ProfileSetValidator.cs` validates profiles against the
  combined device-wide battery and lighting capabilities.
- `src/OpenBlade.SessionAgent` owns host-rendered lighting loops and special-key
  handling.
- `src/OpenBlade.Tray.Core` exposes only capabilities reported by the service.

Keep protocol support, service admission, session rendering, and UI exposure
separate. A discovered value may be documented before it is writable.

## Admission workflow

For every remaining hardware control:

1. Record the exact device, firmware, power source, visible Synapse page,
   saved value, and one intended action.
2. Re-resolve the transient USB address immediately before capture.
3. Capture an idle baseline and one operator-correlated action against only the
   verified controller and device.
4. Record UTC timing, graceful-stop state, byte lengths, hashes, and decoded
   transactions. Keep identifying and raw data private.
5. Preserve failed, contaminated, and timed-out sessions honestly.
6. Derive a candidate only from the exact-device delta.
7. Add a guarded typed query before attempting a write.
8. Establish independent readback and value discrimination.
9. Save the prior value, apply one bounded value, read it back, restore it,
   and independently confirm restoration.
10. Add sanitized evidence and update the coverage ledger.
11. Implement the smallest exact-device factory/port change.
12. Add capability-boundary, failure, rollback, and restoration tests.
13. Run focused tests, the full solution, and the capture-tool suite.

An empty coverage leaf is not completion. Advancing a leaf requires updating
the ledger and its coverage-contract test deliberately.

## Capture safety

- Target only the reverified PID `02C6` device on `\\.\USBPcap2`.
- Treat every USB device address as transient. Address `3` was historical, not
  permanent.
- Never run USBPcap and an OpenBlade HID query or write against the interface
  at the same time.
- Use the private ignored capture broker only while the operator is present.
- Use hidden `-SkipServiceManagement` because the OpenBlade service is absent
  in the capture environment described by the historical sessions.
- Use `-SkipAdministratorCheck` only when the child lacks an administrator
  token and the already-installed capture driver remains accessible.
- Never commit PCAPs, serial numbers, HID paths, device-instance identifiers,
  local usernames, private capture hashes, or broker request files.
- Sanitized annotations must retain enough timing, decode, restoration, and
  failure information for review without retaining unique identifiers.
- Save and restore every user-visible setting. The preferred quiet final
  lighting state is Static at 50% unless the operator requests otherwise.

Continuous Synapse matrix traffic can overwrite the shared feature response.
Reject mismatched transaction, command-class, and command-ID responses. Never
weaken validation to make a contended capture pass.

## Windows Dynamic Lighting

When Windows Dynamic Lighting ownership is selected, OpenBlade must leave
keyboard lighting to Windows and issue no competing background lighting
writes. Switching ownership must stop and dispose the active OpenBlade
renderer before delegation completes.

Production publishing must not emit the retired sparse background-provider
identity, and Session Agent startup must not register it or relaunch under it.
Retain its authoring only as developer evidence. Setup rejects those artifacts
from new payloads, keeps exact publisher-scoped removal for legacy installs,
and can re-register the prior identity only while rolling back a failed update
or uninstall to that legacy tree.

The WDL toggle is an ownership choice, not an educational surface. Keep its UI
short, do not add a redundant Settings launcher, and do not imply that Windows
owns unrelated logo controls.

## UI and capability rules

- Show controls only when the service reports the corresponding capability.
- Hide dependent Gaming policies while Gaming mode is off.
- Keep read-only or unavailable implementation details out of the normal
  control surface.
- Keep update checks and release settings under Settings, not About.
- Preserve the shared card hover and focus behavior. Pointer cancellation,
  capture loss, scrolling, and entry into a different row must clear any stale
  card highlight. Apply the same full-row hover surface to expandable Settings
  headers; the stock WinUI Expander only highlights its chevron.
- Keep model-specific ranges and valid combinations in capability data, not
  duplicated UI constants.
- Do not add explanatory footnotes for normal key behavior.
- Do not hide a discovered feature from the capability model merely because it
  remains read-only.

## Profile and IPC rules

Profile validation is device-wide:

- lighting effects come from the exact lighting capability set;
- fixed and manual-curve fan speeds use the exact `2000–5000 RPM` range;
- Custom performance selections use only the exact admitted CPU/GPU
  combinations;
- dormant values for an unselected mode do not invalidate the profile.

Nested capability objects crossing IPC must pass the same centralized
invariant validation as locally constructed capabilities. Protocol changes
must update contracts, codec tests, public API baselines, and compatibility
behavior together.

## 2026-07-31 audit hardening

The maintainability audit produced these high-level fixes:

- profile validation now composes the exact battery/performance/fan and
  lighting capability sources;
- nested IPC capability validation is centralized instead of being partially
  duplicated by the decoder;
- lighting ownership calls require explicit clients, and WDL delegation stops
  the active renderer first;
- host-rendered lighting presents animated PID `02C6` effects at 30 FPS from
  monotonic deadlines, skips missed deadlines instead of bursting, and
  interpolates captured 40/50 ms oracle samples at presentation time; idle
  audio frames remain suppressed and pulse scheduling wakes against current
  time;
- the playback-mute indicator is gated by an explicit exact-device capability;
- the tray keeps capability-dependent controls concise and restores the shared
  card interaction behavior.

Preserve these less-visible invariants:

- PID `02C6` matrix frames submit six rows and the latch as one ordered,
  non-interleavable SET-only batch. Keep the shared gate, deadline, transaction
  ordering, reusable report buffer, failure poisoning, and no-GET behavior.
- LampArray geometry fingerprints are cached until the enumerated device set
  changes. A periodic observation must not rebuild all lamp geometry when the
  same devices remain present.
- A failed WDL preference save is not safely rolled back unless the previous
  renderer actually resumes. Report both persistence and rollback failures.
- `FanMode.Maximum` uses the legacy `5400` sentinel in saved settings, even
  though this model's independently selectable fixed range ends at `5000`
  RPM. Accept the sentinel only when the profile mode is explicitly Maximum;
  continue rejecting a literal Fixed `5400` request.
- Bounded renderer cleanup succeeds only when the close task completed
  successfully. A faulted task is not a successful drain, even when its
  exception was observed by the pending-disposal helper.
- Capability equality and the exact-device admission gate include
  `SupportsPlaybackMuteIndicator`. For PID `02C6` its expected value is
  explicitly false.
- The first confirmed hardware sample after service startup establishes the
  drift baseline; it does not prove that another controller changed anything.
  `ServiceStartupCoordinator` then explicitly reconciles the active saved
  profile. Only changes observed after that reconciliation are classified as
  external and allowed to pause automatic writes.

These are software hardening changes. They do not advance a hardware evidence
leaf unless the sanitized ledger and its contract test are updated separately.

## Verification

From `openblade-core`:

```powershell
dotnet build OpenBlade.slnx -c Release -p:NuGetAudit=false --no-restore
dotnet test OpenBlade.slnx -c Release -p:NuGetAudit=false --no-build --no-restore
```

From `openblade-captures`, use Windows PowerShell 5.1:

```powershell
& "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" `
  -NoProfile -NonInteractive -ExecutionPolicy Bypass `
  -File .\tests\Run-All.ps1
```

Do not omit `-NonInteractive`; an intentionally missing validation mode can
otherwise prompt and look hung.

Before handoff:

1. Run focused tests for every changed boundary.
2. Run the full build and test commands above.
3. Rerun the capture-tool suite after any toolkit change.
4. Run `git diff --check`.
5. Confirm touched files use the repository line-ending convention.
6. Confirm no raw or identifying artifact is staged.
7. For on-device work, independently read the restored setting and verify the
   OpenBlade service/session processes are in the intended final state.

## Handoff rule

Keep this file concise and current. Put durable leaf status in the sanitized
coverage ledger, technical evidence in sanitized capture annotations, and
chronology only in a new dated implementation note when it is genuinely useful.
The archived log is historical evidence and must not become the active status
source again.
