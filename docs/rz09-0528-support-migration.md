# RZ09-0528 support migration guide

This guide is the handoff record for extending OpenBlade support to the Razer
Blade 16 `RZ09-0528`, BIOS `2.02`, USB `1532:02C6`.

It is intentionally narrower than a compatibility claim. A subsystem is
supported only after its exact-device evidence, independent validation,
restoration, production admission, and tests are complete.

## Working branches

Use `feat/rz09-0528-full-support` in:

- `openblade-core`
- `openblade-captures`
- `openblade-docs`

Keep capture evidence, production code, and public documentation in separate
commits. Never commit raw captures, serial numbers, HID paths, local user paths,
or device-instance identifiers.

## Current production status

Keyboard brightness, the 50-to-80-percent battery-limit ladder, battery
protection off, temporary full charge, all four AC performance presets
including exact-model Custom CPU/GPU combinations, Automatic/fixed/maximum
fan control, startup animation, function-key primary
mode, and Gaming mode are admitted hardware controls for this model. Exact
read-only support also covers fan RPM, device mode, MCU version, and the
current adapter-power class. Windows display controls are admitted through
model-independent operating-system APIs.

| Area | Status | Production scope |
| --- | --- | --- |
| Exact host and HID identity | Admitted | Model, BIOS, PID, usage `0001:0002`, and 91-byte feature reports |
| Keyboard-brightness query | Admitted | `03/83`, arguments `010500` |
| Keyboard-brightness setter | Admitted | `03/03`, arguments `0105xx` |
| Lighting effects and logo | Partially admitted | Keyboard Off and logo Off/Static/Breathing admitted with selector `00`; Spectrum, Static, Starlight, Ripple, Tidal, Wave, and Wheel are explicit observed-only capabilities because their traffic remains non-discriminating |
| Performance and fans | Partially admitted | AC Balanced, Silent, Performance, Custom, exact CPU/GPU combinations, Automatic/fixed/maximum/manual-curve fans, exact 2000-5000 RPM range, and fan RPM; other power-source setters remain unadmitted |
| Sensors | Admitted | CPU temperature through the exact `\_SB.PCI0.SBRG.EC0.TZRZ` Windows thermal-zone counter; the unrelated `\_TZ.TZ01` zone is never substituted |
| Model-specific tuning UI | Observed-only | CPU Power Limit 45-95 W in 1 W steps, CPU Voltage Optimizer -30..-1, and Synapse GPU policy ranges are first-class capabilities. Synapse's CPU backend is identified as RzAMDOverClock/RzDLLService but is not admitted because its refresh lifecycle can write; OpenBlade shows these features without editable controls |
| Power-source matrix | Captured, not admitted | Nine exact-device sessions cover barrel AC, USB-C-only, battery, both quiet baselines, and all directed transitions; exact PID 02C6 USB-C reply and independent thermal readback remain missing |
| Battery charge limit | Admitted | Query, 50/55/60/65/70/75/80-percent setters, protection off, readback, and restoration |
| Temporary full charge | Admitted | Query, enable, cancel, readback, and restoration |
| Keyboard behavior and special keys | Partially admitted | Function-key primary, Gaming mode, startup-animation, and ownership-aware Normal/Driver device-mode setters; special keys pending live decoder validation |
| Display controls | Admitted | Refresh, DRR, HDR state, and SDR color profiles through Windows APIs |
| NVIDIA GPU clock offsets | Query validated, writes gated | Driver query succeeded; exact-model Synapse policy is visible in the UI, but writes remain disabled |

The brightness implementation is in
`src/OpenBlade.Protocol/Rz090528BrightnessLightingFactory.cs`. Service
composition selects it only after the exact `RZ09-0528` host gate passes.

## Brightness evidence chain

Sanitized evidence is stored in the captures repository:

- `annotations/2026-07-29-rz09-0528-keyboard-brightness-admission.json`
- `annotations/2026-07-29-rz09-0528-lighting-query-navigation-negative.json`

The admitted facts are:

- two independent 60-percent captures produced `03/03` arguments `010599`;
- a 70-percent capture produced `03/03` arguments `0105B3`;
- `03/83` independently returned raw `153` at 60 percent and raw `49` at
  19 percent;
- three direct 19-to-60-to-19 OpenBlade round trips restored raw `49`;
- three production-step 19-to-29-to-19 round trips restored raw `49`;
- Synapse page navigation and reload did not issue the brightness query and
  remain negative controls.

Do not replace independent firmware readback with visual Synapse confirmation.

## Battery charge-limit evidence chain

Sanitized evidence is stored in:

- `annotations/2026-07-29-rz09-0528-battery-limit-70-admission.json`
- `annotations/2026-07-29-rz09-0528-battery-limit-matrix.json`
- `decoded/rz09-0528-pid-02c6-bios-2.02-evidence.json`
- `decoded/rz09-0528-pid-02c6-bios-2.02-device-coverage.json`

The exact-device facts are:

- the complete Synapse ladder produced `07/12` arguments `B2`, `B7`, `BC`,
  `C1`, `C6`, `CB`, and `D0` for 50, 55, 60, 65, 70, 75, and 80 percent;
- disabling battery protection produced `07/12 50`;
- `07/92 00` returned `C6` at 70 percent and `D0` at 80 percent three
  times each;
- OpenBlade completed reversible 80-to-70-to-80 and 80-to-75-to-80
  validations in a quiet-controller window;
- with Synapse still running but keyboard lighting switched off by operator
  request, ten queries and two more reversible validations succeeded;
- exact reversible validations succeeded for 50, 55, 60, 65, and 75 percent
  and protection off, each with independent readback and restoration;
- the exact production factory exposes the complete captured ladder plus
  protection off and returned the restored 80-percent state;
- the final firmware and visible Synapse state was restored to 80 percent, and
  keyboard lighting was intentionally left off.

The exact setter encoding is `0x80 + percentage` for the captured 50-to-80
range in five-percent steps. Protection off is `0x50`. Treat that formula as
device-scoped evidence, not a reason to extrapolate beyond the captured values
or to another PID.

Active Synapse Spectrum traffic can overwrite the shared feature response with
an unrelated `03/0B` reply. This appears as `TransactionMismatch`; never weaken
transaction or command validation to hide it. The production path must reject
the unrelated acknowledgement. Do not stop Synapse as routine product
behavior. An operator-controlled developer validation may close and relaunch
it only with explicit authorization and must record that fact.

## Temporary-full-charge evidence chain

Sanitized evidence is stored in:

- `annotations/2026-07-29-rz09-0528-temporary-full-charge-admission.json`
- `decoded/rz09-0528-pid-02c6-bios-2.02-evidence.json`
- `decoded/rz09-0528-pid-02c6-bios-2.02-device-coverage.json`

The exact-device facts are:

- the idle baseline stopped gracefully and contained no setting action;
- one unchecked-to-checked Synapse capture produced `07/8F 00` followed by
  `07/0F 04`;
- one separately prepared checked-to-unchecked capture produced `07/8F 00`
  followed by `07/0F 00`;
- the enable and cancel action PCAPs both stopped gracefully and their hashes
  and byte lengths were preserved privately;
- independent typed queries distinguished inactive `00`, active `04`, and
  restored inactive `00`;
- the reversible validator started at `Limit80` and inactive, enabled and read
  back active, cancelled and read back inactive, then confirmed `Limit80`;
- three exact production-factory probes exposed temporary-full-charge support
  and returned inactive;
- the final visible state was Battery / optimizer 80% / temporary full charge
  unchecked, with actual battery state still on AC at 79%.

The temporary-full-charge production fixtures are exact-device scoped. The
query is `07/8F 00`; enable is `07/0F 04`; cancel is `07/0F 00`. Do not admit
these commands for another PID or BIOS based on this evidence.

## Required workflow for every remaining control

1. Record the exact device, firmware, power source, Synapse version, visible
   page, saved value, and intended single action.
2. Re-resolve the transient USB address immediately before every capture.
3. Capture one idle baseline and one action against only that address.
4. Save hashes, byte lengths, operator-correlated UTC timestamps, shutdown
   state, and decoded transactions.
5. Preserve failed and timed-out sessions honestly.
6. Derive a candidate command only from the exact-device delta.
7. Add a guarded developer-only typed query before any OpenBlade write.
8. Establish independent readback and value discrimination.
9. For a setter, save the prior value, apply one bounded value, read it back,
   restore the saved value, and independently confirm restoration.
10. Add sanitized evidence and update device coverage.
11. Add the smallest exact-device production factory or port. Do not broaden
    the PID `02E0` oracle or copy its device-specific assumptions.
12. Add focused failure, rollback, and capability-boundary tests.
13. Run the affected tests, the full solution, and the capture-tool tests under
    Windows PowerShell 5.1.
14. Commit the evidence and implementation as separate coherent slices.

Never run USBPcap and an OpenBlade HID query or write against the interface at
the same time.

### Elevated capture broker

When the operator is present, front-load the UAC boundary with the private,
ignored `.capture-broker` workflow in `openblade-captures/raw`. The established
broker is fixed to `\\.\USBPcap2`, accepts bounded request files, uses
`-SkipServiceManagement`, and may use `-SkipAdministratorCheck` only when the
child process lacks an administrator token while the already-installed driver
remains accessible.

The broker does not make a transient device address permanent. Immediately
before every request, visually re-resolve the exact `1532:02C6` device in
USBView; address `3` was current during the 2026-07-29 sessions. Never submit a
request if that value no longer matches. Wait for the prior request to finish,
verify that capture is idle, and only then run a direct HID query. Shut down the
broker through its sentinel after the elevated matrix is complete. The broker,
request files, raw PCAPs, hashes that can be correlated to private captures, and
local identifiers must remain ignored and uncommitted.

Before interpreting a query failure, check whether host-rendered Synapse
lighting is continuously issuing `03/0B` traffic. Preserve the failure as
contention evidence. Do not treat a successful quiet-window query as proof that
concurrent traffic is safe.

### Power-source matrix checkpoint

The sanitized matrix is:

- `annotations/2026-07-29-rz09-0528-power-source-matrix.json`
- `decoded/rz09-0528-pid-02c6-bios-2.02-power-source-matrix.json`

Nine successful sessions targeted only the visually reverified
`\\.\USBPcap2` device address 3 and ended through the sentinel-driven graceful
stop path. Two earlier sessions failed before capture readiness because a
USBPcap listing process was active; they remain preserved privately as failures.

The observed source-specific behavior is:

- battery: `On Battery`, Battery Saver, Auto fan, fixed fan unavailable;
- USB-C-only: `Plugged In`, Balanced, Auto fan, Performance and Custom
  unavailable, with the inadequate-power warning visible;
- barrel AC: `Plugged In`, Balanced, Auto fan, Performance and Custom
  available, with no inadequate-power warning.

The outgoing exact-device command correlation is:

- adapter-power query form: `07/8C 0000`;
- Battery Saver/Automatic: `0D/02 01010300` and `01020300`;
- Balanced/Automatic: `0D/02 01010000` and `01020000`.

A later private-transaction re-audit also found exact 16-byte input reports on
endpoint `0x82`: `05/33 0011`, `05/33 0711`, and `05/33 1111`. They correlate
with battery, USB-C, and barrel-AC transitions, respectively. However, the quiet
USB-C no-action baseline also replayed `1111` without a captured `07/8C`
request. Preserve these as candidate source-event reports; they are not an
independently matched feature-query reply and do not admit the generic PID
`02E0` decoder mapping.

The first successful AC-to-USB-C session physically passed through a cable-free
battery interval. Its `On Battery` UI was not delayed USB-C recognition. The
session originally labeled as a USB-C-to-AC restoration was a no-action stable
USB-C baseline; retain that correction in every downstream interpretation.

USBPcap decoded outgoing feature reports plus the candidate input reports. It
did not capture an exact PID `02C6` USB-C `07/8C` reply that OpenBlade
independently matched, nor an independent `0D/82` destination-state readback.
The source-event candidates do not change that gate. Therefore the matrix
advances those coverage leaves to `Captured`, not `QueryValidated`,
`SetterValidated`, or `ProductionAdmitted`. Do not copy the generic PID
`02E0` `0711` USB-C mapping into this device. The next agent must obtain direct
exact-device readback while physically on USB-C and battery before admitting
source-specific setters.

## Remaining full-support matrix

Work through read/query-backed controls before one-shot setters.

1. Battery charge limit and temporary full charge — admitted
2. Performance presets for AC, USB-C, and battery power — all AC modes are
   admitted; USB-C-only Balanced and battery Battery Saver availability plus
   outgoing setter pairs are captured, but exact independent readback is still
   required before those setters can be admitted
3. Automatic, fixed, maximum, and manual-curve fan control plus RPM readback —
   admitted on AC; manual curve is host-rendered through the validated fixed
   target primitive and fresh temperature telemetry
4. Keyboard lighting effects, matrix geometry, cadence, effect getter, and
   logo modes — logo modes and keyboard Off admitted; seven Synapse quick
   effects are represented as observed-only capabilities, while effect-specific
   writes, getter, and matrix geometry remain pending
5. Function-key primary mode, Gaming mode, startup animation, and device mode —
   first three setters admitted; device-mode getter admitted and both exact
   setters reversibly validated, with production mutation still gated
6. Special-key report mapping and default-action preservation
7. Windows display modes, DRR, HDR, and color profiles — admitted through
   Windows APIs; HDR-on behavior now has a bounded save/readback/restore
   validation and explicitly disables SDR color-profile writes
8. Conflict, timeout, malformed-response, disconnect, and lifecycle validation
   — exact service restart is admitted with independent brightness readback
9. Cold boot, sign-in, sleep/resume, and reconnect validation
10. AC, USB-C, and battery transition validation — the physical/UI/USBPcap
    matrix is captured; exact PID `02C6` USB-C `07/8C` response and independent
    destination-state readback remain pending

Display controls implemented entirely through Windows APIs may become
`SoftwareOnly` after exact-machine validation; they must not be described as
firmware commands.

## Completion gate

Full support is complete only when every user-visible capability exposed for
this model is either:

- production-admitted with exact-device evidence and on-device validation;
- explicitly `SoftwareOnly` with Windows API validation; or
- explicitly absent on this hardware and not exposed.

An empty or uninvestigated matrix entry is not completion.

## Verification commands

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

Do not omit `-NonInteractive` in automation. The capture-evidence regression
deliberately tests a missing mandatory validation mode; an interactive host
prompts instead of throwing and appears to hang.

Before each commit, inspect every touched file for its declared line-ending
convention and run `git diff --check`.

## Commit checkpoints

- `openblade-captures` `eaf1c41`: PowerShell 5.1 transaction-tool fixes
- `openblade-captures` `95098b6`: sanitized brightness evidence
- `openblade-core` `ce2fd41`: exact-device brightness production admission
- `openblade-core` `ad39ca5`: initial future-agent migration guide
- `openblade-captures` `56b9881`: sanitized limit-70 evidence and exhaustive
  device coverage
- `openblade-core` `34cb0c0`: exact-device battery query, validation tooling,
  and limit-70 production admission
- `openblade-captures` `f984c01`: sanitized temporary-full-charge enable,
  cancel, query, readback, and restoration evidence
- `openblade-core` `6cf04ef`: exact-device temporary-full-charge production
  admission and developer validation entry points
- `openblade-captures` `4d3c128` and `openblade-core` `261046f`: AC Balanced
  and Silent capture evidence, typed readback, setters, and restoration
- `openblade-captures` `35f334d` and `openblade-core` `094397b`: AC
  Performance capture evidence, admission, and restoration
- `openblade-captures` `da15d62`: partial Custom CPU/GPU evidence; Custom
  remains unadmitted
- `openblade-captures` `56d8b2e` and `openblade-core` `9c86633`: exact-device
  fan-RPM query evidence and production routing
- `openblade-captures` `a094424` and `openblade-core` `24ea042`: startup
  animation readback
- `openblade-captures` `3e52bac` and `openblade-core` `00693bf`: function-key,
  Gaming-mode, and device-mode readbacks
- `openblade-captures` `55bd102` and `openblade-core` `540a897`: EC/MCU
  evidence and MCU production readback
- `openblade-captures` `73f3272` and `openblade-core` `c9147a7`: full-AC
  adapter-class readback
- `openblade-core` `25620fe`: exact RZ09-0528 production-read regression guard
- `openblade-captures` `c9389c0`, `openblade-core` `52fd4d6`, and
  `openblade-docs` `6d29375`: exact-machine display validation and publication
- `openblade-captures` `3cd2182` and `openblade-core` `1f25572`: read-only
  NVIDIA clock-offset capability probe and guarded coverage record
- `openblade-captures` `bf9c3d7` and `openblade-core` `38148fd`, `579a49e`,
  and `29386c7`: exact fixed-fan capture, device range contract, and fixed-fan
  admission
- `openblade-core` `4895f13`: propagate the exact 2000-5000 RPM, 100-RPM-step
  range through service, diagnostics, and tray UI
- `openblade-captures` `de97f7a`: elevated AC control matrix, including
  manual-curve selection and a second Battery-to-Lighting negative control
- `openblade-captures` `2dbd30d` and `openblade-core` `2929c12`: complete
  battery-limit/protection-off matrix and production admission
- `openblade-core` `233ae9a`: exact startup, function-primary, and Gaming-mode
  setter admission with bounded one-shot validation
- `openblade-captures` `f4e333c`: exact selector-`00` logo queries,
  discriminating readback, bounded round trip, and restoration evidence
- `openblade-core` `d452989`: rollback-safe exact keyboard-Off and logo
  production admission, bounded validator, and full regression coverage
- `openblade-docs` `786b98e`: publish the expanded exact-device compatibility
  and protocol summary while retaining `In development` status
- `openblade-core` `fef9944`: preserve seven captured Synapse quick effects as
  observed-only capabilities across IPC, diagnostics, and tray UI without
  adding them to the writable effect set
- `openblade-captures` `9da267e` and `openblade-docs` `6512f86`: align
  sanitized coverage and public protocol documentation with the observed-only
  quick-effect boundary
- `openblade-core` `4e09376`: make the safe production probe verify the current
  four AC presets, four fan modes, and Battery/USB-C observations
- `openblade-core` `58d981e`: publish capability-only changes instead of
  silently suppressing observed lighting, power-source, logo, laptop-control,
  or keyboard-brightness changes
- `openblade-core` `3b76ae6`: advance firmware-smoke snapshots to schema 2 and
  treat loss of observed lighting effects as a post-update regression
- `openblade-core` `40b4be0`: reconcile Windows HDR setter results against
  independent readback and add a bounded save/set/readback/restore validator
- `openblade-core` `ce7170a`, `openblade-captures` `5df3b67`, and
  `openblade-docs` `6ebdfa4`: record and publish the successful reversible HDR
  validation while preserving the HDR-on SDR-profile exclusion
- `openblade-captures` `d1eb8f5`: classify deterministic malformed-response,
  checksum, command-mismatch, status-failure, disconnect, and cancellation
  coverage as software-only fail-closed validation
- `openblade-core` `9711409`: add an exact PID `02C6` raw-input probe instead
  of silently filtering this model through the legacy PID `02E0` target
- `openblade-captures` `6311ad0`, `a3211a0`, and `cb5f7c3`: enforce the shared
  coverage-status vocabulary, advance already-proven software-only leaves, and
  preserve the newly re-audited source-event report candidates

Verification at the battery checkpoint:

- Windows PowerShell 5.1 capture toolkit: all tests passed;
- sanitized schema-2 annotation: valid, with private PCAP hash verified;
- focused RZ09-0528 battery tests: 3 passed;
- full solution build: 0 warnings and 0 errors;
- full solution tests: 3,165 passed after removing a temporary diagnostic field
that changed record equality; the final rebuild also completed cleanly.

Verification at the temporary-full-charge checkpoint:

- enable capture: 2,438 bytes, bounded timeout with graceful stop, sanitized
  schema-2 annotation valid and private PCAP hash verified;
- cancel capture: 2,438 bytes, bounded timeout with graceful stop, decoded
  `07/8F 00` followed by `07/0F 00`;
- independent readback: inactive, active, then restored inactive;
- reversible validation: `Limit80/False` to `Limit80/True` to
  `Limit80/False`, with restoration confirmed;
- exact production factory: three successful read-only probes with temporary
  full charge inactive;
- focused exact-device protocol tests: 3 passed;
- full Release build: 0 warnings and 0 errors;
- full solution tests: 3,165 passed;
- Windows PowerShell 5.1 capture toolkit: all tests passed.

Performance discovery checkpoint:

- Battery-to-Performance navigation was a negative control: it produced no
  feature traffic on the captured interface because Synapse had cached the
  page state.
- The developer-only discriminator must use
  `CreateWindowsRz090528PerformanceFanDiscriminatorQuery`; the generic factory
  targets RZ09-0581 / PID `02E0`.
- `PerformanceFanDiscriminatorQueryCommand` previously hardcoded the generic
  device identity in its JSON. It now accepts an internal explicit identity;
  never accept a result whose embedded device provenance disagrees with the
  exact factory and CLI target.
- At AC Balanced + Auto, `0D/82` readback is `01010000` and `01020000`. At AC
  Silent + Auto, it is `01010500` and `01020500`.
- Valid host-token Synapse captures decoded paired `0D/02` writes:
  Silent `01010500`, `01020500`; Balanced restoration `01010000`,
  `01020000`.
- Silent capture: 2,810 bytes, SHA-256
  `C37066107F567ED19E56329EE5A1FA790DE535A218AE12374C3BBECE6B010833`.
- Balanced restoration capture: 2,810 bytes, SHA-256
  `00B866C6B426807FBBB7F8FB73B09634AD2F8109850BF6BD951F6F298E894A32`.
- Both captures ended through bounded timeout with graceful stop. The earlier
  sandbox-token attempts reported ready but produced no PCAP; their failed
  state is preserved privately and they are not evidence.
- Exact production-port validation confirmed
  `Balanced/Automatic -> Silent/Automatic -> Balanced/Automatic`, and three
  subsequent production probes reported the restored state.
- Only AC Balanced, AC Silent, and Automatic fans are admitted at this
  checkpoint. Performance, Custom, battery presets, fixed/manual/maximum fans,
  and RPM readback still require exact-device evidence.
- CPU Voltage Optimizer remained enabled at its pre-existing visible value and
  was never changed.

AC Performance follow-up:

- Performance + Auto captured `0D/02 01010200` and `01020200`; exact `0D/82`
  readback returned the same `02` mode on both channels.
- The action capture was 2,810 bytes with SHA-256
  `61289DAF0E95BA21A6E7661A35272DCF1A2CD35FEDB3C5BE8CE0AF51012FC8D0`.
- The separate Balanced restoration capture was 2,810 bytes with SHA-256
  `550E8A0BE710A6DEBF9F1BEFC83D5DEF89CDB6D6B1B4D77A3DE07719530DAEE3`.
- Both captures timed out through the normal graceful-stop path.
- Exact production-port validation confirmed
  `Balanced/Automatic -> Performance/Automatic -> Balanced/Automatic`.
- AC Balanced, Silent, Performance, and Automatic fans are admitted. Custom,
  battery presets, fixed/manual/maximum fans, and RPM readback remain pending.

AC Custom partial follow-up:

- Selecting Custom on AC emitted paired `0D/02` arguments `01010400` and
  `01020400`, then reapplied saved component values with `0D/07`.
- On this exact PID, visible CPU Medium reads back as `010102`. This differs
  from the existing PID `02E0` assumptions and must remain device-scoped.
- Visible GPU Low, Medium, and High map to `010200`, `010201`, and `010202`.
  All three were independently captured and read back, including a clean
  High-to-Low restoration.
- CPU Low and High could not be exercised because Synapse disabled the CPU
  preset controls while CPU Voltage Optimizer was enabled. The optimizer was
  deliberately preserved. Custom and its component setters therefore remain
  outside the production factory despite the partial evidence.
- Timeout-bound sessions that ended before a delayed UI click were preserved
  as negative controls rather than attributed to the later click.
- Final state was restored to AC Balanced, Auto (Default); typed `0D/82`
  readback returned `01010000` and `01020000`. Saved component readback
  remained CPU `010102` and GPU `010200`.

Fan RPM readback follow-up:

- The existing read-only `0D/88` selector protocol was routed through the
  exact RZ09-0528/PID `02C6` actor factory rather than the PID `02E0` factory.
- Three independent probes succeeded. Observed selector pairs were
  `2600/2500`, `2500/2200`, and `2500/2200` RPM while the fan mode remained
  Automatic.
- The service composition now selects the exact-device fan query factory on
  this host. The exact production-admission probe returned `2300/2100` RPM,
  `Failure=None`, and `Success=True`.
- The production probe also reconfirmed access fallback, three admitted AC
  presets, Automatic-only fan writes, limit 70, temporary full charge,
  brightness raw 0/0 percent, and no admitted effect or logo writes.
- Fixed speed, maximum, and manual fan curves remain pending exact setter and
  restoration evidence.

Startup-animation readback follow-up:

- The exact discriminator's `0F/98 00` response decoded as enabled under the
  existing typed protocol.
- Startup-animation query evidence was added only to the exact RZ09-0528
  production read set. The production service probe reopened the exact battery
  port, returned `StartupAnimation=True`, and completed with `Success=True`.
- No startup-animation setter is admitted. Enable/disable writes still require
  exact setter capture, independent readback, and restoration.

Additional keyboard-behavior readbacks:

- Exact advisory probes returned `02/86 0100` = FunctionKeys,
  `00/88 00000000` = Gaming Mode disabled, and `00/84 0300` = Driver device
  mode.
- Candidate probe JSON now accepts explicit device identity; do not allow
  exact PID `02C6` results to be mislabeled as the PID `02E0` target.
- Function-key and gaming-mode read evidence was added only to the exact
  RZ09-0528 production battery factory. Device-mode query-only support was
  added to the exact brightness lighting port; it does not claim ownership or
  write Normal/Driver mode.
- The exact production probe reconfirmed FunctionKeys, Gaming Mode false, and
  Driver mode true with `Success=True`.
- All corresponding setters remain unavailable until exact captures and
  reversible validation exist.

Firmware readback follow-up:

- Windows firmware metadata reported EC `1.09`; no system SKU or other
  potentially identifying registry value was committed.
- The exact discriminator added read-only `00/87 00000000` and returned
  `01090000`, decoded by the existing typed protocol as MCU `1.9.0.0`.
- MCU evidence was admitted only in the exact RZ09-0528 production read set.
  The exact production probe returned `McuVersion=1.9.0.0` and
  `Success=True`.

Adapter-power readback follow-up:

- The exact discriminator added read-only `07/8C 0000` and returned `1111`,
  decoded by the existing typed protocol as `FullPower280W`.
- Adapter-power evidence was admitted only in the exact RZ09-0528 production
  read set. The production probe reconfirmed `AdapterPower=FullPower280W` and
  `Success=True`.
- This proves the current full-AC class only. USB-C 100 W, battery operation,
  and every power-source transition remain pending physical-state evidence.

Display capability follow-up:

- The existing production display query successfully selected the internal
  panel and reported `2560x1600`.
- Available refresh choices were fixed 60 Hz, fixed 240 Hz, and Dynamic
  48-240 Hz. The observed current mode was fixed 60 Hz.
- HDR was supported and observed off. SDR color-profile control reported
  Native, DCI-P3, sRGB, Rec. 709, and Adobe RGB, with Native active.
- The first bounded HDR attempt exposed a Windows return-code anomaly: the
  native call reported access denied after readback showed HDR on, and the
  matching restoration call likewise left the saved HDR-off state restored.
  Production now reconciles a native failure only when a fresh query confirms
  the exact requested state; disagreement remains a failure.
- A repeated timed validation from `2026-07-29T21:21:08.1366040Z` through
  `2026-07-29T21:21:08.3525889Z` confirmed
  `Off -> On -> Off`, `Applied=True`, `ReadBack=True`, `Restored=True`, and
  `Success=True`. While HDR was on, `ColorProfilesEnabled=False`; no profile
  write was attempted. The final query reconfirmed fixed 60 Hz, 2560x1600,
  HDR off, and all five SDR profiles.
- These model-independent Windows display paths are production-admitted for
  RZ09-0528. HDR-mode color-profile controls are explicitly unavailable rather
  than an uninvestigated feature.

NVIDIA GPU clock-offset follow-up:

- The read-only NVML probe selected the NVIDIA GeForce RTX 5090 Laptop GPU and
  reported current core and memory offsets of 0 MHz.
- The NVIDIA driver reported a core range of -1000 to 1000 MHz and a memory
  range of -2000 to 6000 MHz. Synapse exposes the narrower exact-model policy
  ranges core -1000 to +400 MHz and memory -1000 to +3000 MHz.
- No offset was written and no visible setting changed.
- The policy ranges are first-class device capabilities and appear in the UI,
  but exact-model writes remain disabled. A read-only NVML range is not setter
  validation, and OpenBlade must not silently substitute the broader driver
  range for the vendor's narrower exact-model policy.
- The bounded
  `validate-rz09-0528-02c6-gpu-clock-offsets
  RZ09-0528:1532:02C6:GPU-OFFSETS:RESTORE` command is the next setter gate.
  It requires the exact host and one exact PID `02C6` protocol collection,
  refuses to run while Synapse AppEngine, either verified Razer controller
  service process, or the OpenBlade service is active, and rechecks those
  isolation conditions immediately before the write. It saves
  both offsets, chooses a one-MHz adjacent value inside the narrower
  exact-model policy for both channels, sets both, independently reads both
  back, and unconditionally restores and verifies the saved pair. Its
  transactional controller also rolls back a partial core/memory write.
  A dry invocation with both controllers active proved
  `MutationAttempted=False`; that is interlock evidence, not setter evidence.
  Keep `gpuClockOffsets=QueryValidated` and GPU overclock access observed-only
  until an isolated live run reports `Applied=True`, `ReadBack=True`,
  `Restored=True`, and `Success=True`.
- `openblade-captures/tools/Invoke-Rz090528GpuClockOffsetValidation.ps1`
  packages that run into one elevated Windows PowerShell 5.1 session. It
  verifies the installed service executables and the signed Synapse app-engine
  path before stopping anything, records only the verified services/process
  IDs, restores every initially running service and Synapse from `finally`,
  and hashes the private output. Pass `-IncludeSpecialKeyProbe` only when the
  operator is present; it uses the same isolated window for one bounded
  60-second PID `02C6` special-key retry and hashes that private output
  separately.
- The first fully isolated live run saved 0/0, requested the bounded adjacent
  pair +1/+1, and received no confirmed applied state. Its internal rollback
  independently read 0/0 again, and the wrapper restored OpenBlade, both Razer
  services, Synapse, and the approximately 50-percent Static keyboard state.
  Preserve this as `ReadbackNotConfirmedRestored`: it proves rollback and
  interlocks, not setter support. Keep `gpuClockOffsets=QueryValidated` and
  GPU overclock access observed-only.
- Synapse had GPU Overclock disabled for that run. This is a plausible
  prerequisite, not a proven cause. The next safe experiment must visibly
  preserve, enable, and restore that separate toggle before repeating one
  adjacent offset round trip; never brute-force an undocumented step or infer
  support from setter return values without independent readback.

Elevated capture follow-up:

- The initially unapproved fixed-fan session remains preserved privately as
  `Failed` with no PCAP and is not evidence.
- After the operator returned, one UAC approval started the private elevated
  broker and front-loaded the remaining Synapse action captures.
- Every request targeted only `\\.\USBPcap2` after visually rechecking exact
  PID `02C6` at transient address `3`. Every bounded session ended through the
  graceful timeout path and was decoded with Windows PowerShell 5.1.
- The clean Battery-to-Lighting navigation capture again contained no device
  command envelope and no `03/83 010500`. It is a second negative control, not
  readback evidence.

Battery and keyboard-behavior setter follow-up:

- The captured battery ladder is 50=`B2`, 55=`B7`, 60=`BC`, 65=`C1`,
  70=`C6`, 75=`CB`, and 80=`D0`; protection off is `50`.
- Direct reversible validations for 50, 55, 60, 65, 75, and protection off all
  reported `Applied=True`, `ReadBack=True`, `Restored=True`, and
  `Success=True`.
- Startup animation uses exact setter `0F/18`, arguments `0001` for off and
  `0000` for on.
- Function-key primary uses exact setter `02/06`, arguments `0000` for
  Function Keys and `0001` for Multimedia Keys. PID `02E0` uses
  `0100`/`0101`; copying that encoding to PID `02C6` would be incorrect.
- Gaming mode uses exact setter `00/08`, arguments `01000000` for on and
  `00000000` for off.
- Bounded production validations restored startup enabled, Function Keys
  primary, and Gaming mode disabled. A final read-only production probe
  returned all three restored values with `Success=True`.

Lighting capture follow-up:

- Exact logo writes use selector `00`: Static emitted `03/02 000400` followed
  by `03/00 000401`; off changed the latter to `000400`; Breathing used
  `03/02 000402`.
- This differs from the PID `02E0` selector `01` assumption. Logo support
  queries use `03/80 000400` and `03/82 000400`.
- The exact production path completed
  `Static -> Breathing -> Off -> Static`, independently read each state, and
  confirmed final restoration. Keyboard Off and logo Off/Static/Breathing are
  production-admitted; no animated keyboard effect is implied.
- Quick-effect transitions among Spectrum, Static, Starlight, Ripple, Tidal,
  Wave, and Wheel produced only generic Chroma traffic and no reliably
  effect-discriminating payload. They remain captured, not admitted.
- `DeviceCapabilities.ObservedLightingEffects` preserves all seven discoveries
  separately from writable `LightingEffects`. The tray identifies them in an
  informational card, diagnostics emit both writable and observed lighting
  keys, and the effect picker still contains only independently admitted
  effects. Firmware-smoke schema 2 also records the observed set and treats a
  post-update loss as a capability regression. Adding this strict contract
  field advanced service IPC from version 26 to 27.
- A read-only exact PID scan found ten HID collections but no 375-byte feature
  collection. The exact device has the 91-byte control collection and a
  51-byte usage-`0059:0001` collection; do not route PID `02C6` through the
  PID `02E0` matrix writer.
- The final visible keyboard quick effect was restored to Static. Keyboard
  brightness was restored to its saved off value.

Custom performance and model-specific power-control follow-up:

- Controlled captures established the exact CPU mapping: Low=`00`,
  Medium=`01`, and High=`02` in `0D/07 0101xx`. The earlier partial-session
  note correlating visible Medium with raw `02` was wrong; raw `02` is High.
- GPU Low, Medium, and High likewise map to `00`, `01`, and `02` with selector
  `0102`.
- RZ09-0528 permits CPU High with GPU Low only. This is represented by an
  exact-device allowed-combination capability, not by changing the generic
  PID `02E0` rule.
- Production round trips applied and independently read back Low/Low,
  High/Low, and Medium/High. Every run restored the original
  Balanced/Automatic state. A timed Low/Low repeat ran from
  `2026-07-29T19:38:18.338Z` to `2026-07-29T19:38:20.138Z` and also restored.
- Disabling CPU Voltage Optimizer exposed CPU Power Limit 45–95 W with a
  validated 1 W UI step. No target PID `02C6` transaction appeared, so the UI
  documents the capability but does not offer a write or claim readback.
- CPU Voltage Optimizer exposes -30 through -1 and was restored On at its saved
  `-1.000` value. Its observed `0D/07` writes collide exactly with CPU preset
  setters and are not admitted as optimizer protocol.
- Static inspection of Synapse 4.0.698.98 identified the actual CPU-control
  backend as `RzAMDOverClock_v1.1.15.0.dll` communicating with
  `RzDLLService_v1.0.29.0.exe` over a named pipe. Its FFI surface includes
  asynchronous power-limit and curve-optimizer getters and setters plus an
  explicit callback-registration lifecycle. This supersedes the earlier
  “backend unidentified” note; it does not admit the proprietary interface.
- Sanitized vendor logs recorded 45 successful curve-optimizer reads returning
  `-1`, three returning `0`, three successful automatic writes of `-1`, and
  seven successful reapplications of the persisted 60 W limit. Cached Synapse
  JavaScript confirms that a `0` optimizer response is converted to `-1` and
  written, and that a power-limit response which differs from persisted state
  schedules a setter. A Synapse refresh is therefore a mutating workflow, not
  independent readback.
- Do not call a vendor getter through Synapse and label the result read-only.
  A future OpenBlade backend must own an isolated `DeviceInit` / callback
  registration / getter-or-setter / unregister / `DeviceTerminate` lifecycle,
  must prevent Synapse from racing or restoring its own persisted value, and
  must save, independently read back, and restore the original setting.
  Until that is demonstrated, both CPU controls remain observed-only UI.
- The first OpenBlade getter-only probe proved this is not theoretical. At
  `2026-07-29T20:50:09.015Z` it invoked only `GetPowerLimitValueAsync`; the
  backend returned 55 W at `20:50:09.402Z` while Synapse persisted 60 W.
  Synapse's active listener called `SetPowerLimitValueAsync(60)` at
  `20:50:09.912Z`, and restoration succeeded at `20:50:10.379Z`. OpenBlade
  neither bound nor invoked a setter export, but the shared response still
  caused a write. Its own callback timed out and the client was torn down at
  `20:50:19.022Z`.
- Preserve that run as a failed, mutating discovery session. The exact-hash
  capture command now checks for `RazerAppEngine` before loading the vendor
  DLL and again immediately before its first getter; if Synapse is active it
  fails before issuing a request. Do not weaken or bypass this interlock.
- An isolated follow-up shut down only the verified Synapse
  `RazerAppEngine.exe` family. When its last clients disconnected at
  `2026-07-29T20:54:56.172Z`, the generic service closed its named-pipe server
  and terminated the AMD module even though the `RzDLLService` process
  remained. The exact getter at `20:55:11.588Z` was rejected before a request
  could be written; no setter was bound or invoked.
- OpenBlade must not start `RzDLLService`, initialize its privileged tuning
  driver, or bypass the Synapse-process guard merely to make this probe pass.
  Those are a separate backend-ownership feature requiring explicit lifecycle
  design, crash recovery, conflict exclusion, version/hash admission, and
  save/readback/restore validation.
- Synapse was relaunched after the isolated failure. At
  `2026-07-29T20:56:33.782Z`, its own startup getter returned optimizer `0`;
  the saved-value handler successfully restored `-1` at `20:56:34.552Z`.
  At `20:57:00.239Z`, the visible state was reconfirmed as Plugged In,
  Balanced, Automatic fans, and CPU Voltage Optimizer On at `-1.000`, then
  the app was returned to the tray.
- GPU Overclock was toggled only long enough to inventory the exact-model
  policy ranges; both offsets remained zero and the toggle was restored Off.
- Future agents must add newly discovered model features to the capability
  contract, diagnostics, documentation, and gated UI even when the backend is
  unavailable. “Observed only” is an explicit state; silently omitting the
  feature is not acceptable.

Power-source behavior contract checkpoint:

- `DeviceCapabilities.ObservedPowerSourceBehaviors` is deliberately separate
  from the writable `PerformancePresets` and `FanModes` collections. An
  observation must never make its selected preset or fan mode commandable.
- Exact RZ09-0528 observations record Battery Saver plus Automatic fans on
  battery, and Balanced plus Automatic fans on USB-C. Fixed fan control,
  Performance, and Custom were visibly unavailable in both states; USB-C also
  showed Synapse's inadequate-power warning.
- The tray surfaces the matching observation as a read-only card only while
  that source is active. The card states that this is captured UI evidence and
  that no write path is exposed without exact-device readback.
- The observations round-trip over IPC, appear in diagnostic archives, reject
  duplicate source entries, and participate in status equality. Adding the
  power-source contract field advanced the strict IPC version from 25 to 26;
  the later observed-lighting contract field advanced it from 26 to 27.
- Do not populate unobserved choices. In particular, the existing captures do
  not establish Battery Balanced as available on PID `02C6`.
- For the next physical source session, run the exact read-only
  `query-rz09-0528-02c6-power-source` command. It sends only `07/8C 0000` and
  the two `0D/82` thermal queries, emits sanitized exact-device JSON, and
  performs no writes. Preserve the raw responses; do not reduce visual
  Synapse confirmation to protocol readback.
- The first live invocation on barrel AC succeeded without elevation and
  returned adapter `1111` plus thermal pairs `01010000` and `01020000`,
  reconfirming the full-AC Balanced/Automatic baseline. The command's
  `productionAdmitted: false` marker means the source-specific interpretation
  is still discovery evidence; it does not mean the fixed read-only query
  envelopes are arbitrary.
- Keep raw decoding separate from production admission. The shared adapter
  decoder recognizes `0711` as USB-C for PID `02E0`, but RZ09-0528 has only
  validated `1111` full AC. Its production factory therefore filters every
  other recognized adapter state to `Unknown`; the exact raw probe still emits
  the bytes needed to admit a future PID `02C6` USB-C response.
- For the next special-key session, run
  `probe-rz09-0528-02c6-special-keys [seconds] [output-file]`. The older
  `probe-special-keys` command intentionally retains its PID `02E0` target.
  The exact command records raw HID reports and scan codes without translating
  text or taking ownership of the control interface. Do not add a PID `02C6`
  decoder until all five operator-correlated press/release reports and default
  actions are captured.

Deterministic HID failure-safety checkpoint:

- The exact RZ09-0528 ports use the shared 91-byte production actor. Its
  deterministic tests reject a malformed report ID or report length, checksum
  mismatch, command-class mismatch, command-ID mismatch, non-success firmware
  status, transport failure, and cancellation.
- An ambiguous transaction poisons the actor session. The production path does
  not retry a write whose acknowledgement cannot be matched, because replaying
  an unknown write result could duplicate a firmware mutation.
- These coverage leaves are `SoftwareOnly`, not hardware admissions. No
  malformed report was injected into the physical laptop merely to exercise a
  failure path, and no test result should be described as exact-device response
  evidence.
- Future ports must preserve this fail-closed behavior and add deterministic
  tests for any new report geometry or transaction mechanism before exposing a
  setter.

Final user-visible state before quiet HID validation:

- Battery optimizer enabled at 80 percent; temporary full charge disabled.
- AC Performance Balanced; fans Automatic.
- CPU Voltage Optimizer enabled at its pre-existing `-1.000` value.
- Keyboard brightness off; keyboard quick effect Static (green); logo Static.
- Startup animation enabled; Function Keys primary; Gaming mode disabled.
- Synapse was relaunched after quiet HID validation and is minimized at this
  checkpoint. No OpenBlade service is installed.

Latest verification after the battery, fan-range, and behavior checkpoints:

- full Release build: 0 warnings and 0 errors;
- service tests: 684 passed;
- tray tests: 613 passed;
- protocol tests: 1,046 passed after adding the focused power-source query
  sequence guard;
- capture tests: 223 passed;
- Windows PowerShell 5.1 capture-evidence, comparison, and device-coverage
  regression suites: all passed;
- contracts tests: 188 passed after adding power-source observation IPC
  coverage;
- full solution tests: 3,182 passed across all ten Release test projects;
- exact production read-only probe: battery 80/on, temporary full charge off,
  current barrel AC `FullPower280W`, brightness raw 0, startup enabled,
  Function Keys primary, Gaming mode off, Driver device mode, MCU `1.9.0.0`,
  logo Static, four AC performance presets including
  Custom, four fan modes, keyboard Off plus three logo modes, and
  `Success=True`.
- final read-only production probe at commit `fb3f008` returned
  `ReadyWithAccessFallback`, no open/query failures, fan RPM `0/0` while the
  fans were stopped, and `Success=True`; it issued no hardware writes;
- documentation site validation resolved all local links and assets across 11
  HTML pages;
- power-source evidence and regression commits:
  `openblade-captures` `62e86d3`, `openblade-core` `0d39d10`, and
  `openblade-docs` `7892c2b`;
- PowerShell noninteractive invocation guidance:
  `openblade-captures` `0d40759` and `openblade-core` `fb3f008`.
- source-observation contract and UI: `openblade-core` `51dcba2`; the strict
  service IPC version advanced to 26 at that checkpoint;
- observed quick-effect contract and UI: `openblade-core` `fef9944`; the strict
  service IPC version is now 27, writable lighting remains keyboard Off, and
  2,536 targeted Contracts, Protocol, Service, and Tray tests passed;
- final Release build at `3b76ae6`: 0 warnings and 0 errors;
- final full Release suite: 3,198 tests passed across all ten projects;
- `dotnet format OpenBlade.slnx --verify-no-changes --no-restore`: clean after
  normalizing the touched Windows source files to their declared CRLF policy;
- Windows PowerShell 5.1 capture-toolkit regression: all four suites passed;
- documentation validation: all local links and assets resolved across 11
  HTML pages;
- final safe read-only exact-device probes reported battery protection on at
  80%, temporary full charge off, Balanced/Automatic, four admitted AC
  presets, four admitted fan modes, Battery/USB-C observations, fan RPM
  2700/2500, keyboard brightness raw 0, logo Static, Driver device mode,
  adapter response `1111`, and thermal responses `01010000`/`01020000`.
  Every probe returned success and no setter was invoked;
- focused exact-device source query: `openblade-core` `9a30aa8`; its live
  barrel-AC run returned `1111`, `01010000`, and `01020000` with
  `success: true`;
- public source-observation and probe documentation: `openblade-docs`
  `d3747f1`;
- post-change full Release build: 0 warnings and 0 errors;
- post-change tests: 3,185 passed across all ten projects (Tray 613, Setup
  206, SessionAgent 147, Session 33, Service 684, Display 35, Capture 223,
  FirmwareSmoke 10, Contracts 188, Protocol 1,046);
- post-change Windows PowerShell 5.1 capture toolkit: all four regression
  suites passed with `-NonInteractive`;
- final post-build production probe returned `ReadyWithAccessFallback`, no
  open/query failures, fan RPM `2100/2100`, brightness 0 percent, full-power
  280 W AC, the restored keyboard/startup/logo states, and `Success=True`;
- the remaining source-admission blocker is physical evidence: run the new
  read-only probe while truly USB-C-only and while cable-free on battery.
  Do not admit PID `02C6` source setters from the existing UI observations or
  outgoing capture traffic alone.

Latest post-HDR handoff verification:

- full Release build: 0 warnings and 0 errors;
- full Release suite: 3,204 tests passed across all ten projects
  (Display 38, Setup 206, Protocol 1,049, Service 685, SessionAgent 147,
  FirmwareSmoke 11, Contracts 190, Capture 232, Tray 613, Session 33);
- `dotnet format OpenBlade.slnx --verify-no-changes --no-restore`: clean;
- Windows PowerShell 5.1 capture-toolkit regression: all four suites passed;
- documentation validation: all local links and assets resolved across 11 HTML
  pages;
- the final non-mutating reconnection probe returned adapter `1111` and thermal
  pairs `01010000` and `01020000`, confirming the full-power AC
  Balanced/Automatic baseline;
- Synapse was returned to the tray after the probe. The saved visible state
  remains keyboard brightness off, keyboard Static, logo Static, battery
  protection 80%, temporary full charge off, Balanced/Automatic, CPU Voltage
  Optimizer on at `-1.000`, and GPU overclock off. No OpenBlade service is
  installed.

When invoking the capture-tool suite from a Node or other redirected-process
harness, retain `-NonInteractive` or close stdin explicitly. Leaving a writable
stdin pipe open reproduces the intentional missing-parameter prompt and makes
the test process appear hung even though the validator has not failed.

## Use Windows LampArray for the PID 02C6 matrix

Do not interpret the missing PID `02E0`-style 375-byte feature collection as
proof that RZ09-0528 lacks individually addressable keyboard lighting. Windows
Dynamic Lighting exposes the exact `1532:02C6` keyboard through
`Windows.Devices.Lights.LampArray`.

The read-only exact-device probe
`probe-rz09-0528-02c6-lamp-array` established:

- one exact `Keyboard` array with 85 lamps;
- connected and enabled state, RGB control purposes, and virtual-key support;
- a 33,333 microsecond minimum update interval;
- a `0.308 x 0.110 x 0.015` metre bounding box;
- 72 non-empty virtual-key mappings and six lamps without a virtual-key
  mapping;
- sanitized geometry-and-key-map SHA-256
  `C82E71D69DCAADDCB8530C0744B7B3240CC2E9C8FEF6CDAF1AB16B76F7A6A450`.

The probe deliberately excludes `DeviceInformation.Id`, the device name, HID
instance suffixes, and serial data. It calls no color, brightness, effect, or
priority mutation API. The canonical sanitized evidence is
`openblade-captures` commit `151a632`; the core regression suite carries a
self-contained checkpoint fixture so a clean `openblade-core` checkout does
not accidentally depend on the ignored local `captures` directory.

Keep these boundaries explicit:

- `LampArray.BrightnessLevel` is an OS lighting value, not firmware command
  `03/83` readback.
- `keyboardLighting.matrixInterfaceDiscovery=Absent` continues to describe the
  older raw 375-byte transport.
- `keyboardLighting.lampArrayInterfaceDiscovery=QueryValidated` describes the
  exact OS-native transport.
- Read-only discovery does not admit production writes.

Windows remains the production LampArray owner. `OpenBlade.SessionAgent` may
observe the exact array read-only for capability presentation, but production
publishing must not request background-controller package identity or register
the `com.microsoft.windows.lighting` extension. The signed sparse-identity
experiment proved that doing so adds OpenBlade as a competing provider rather
than improving device support.

The supported integration is explicit delegation:

1. expose the Windows ownership option only for an exact observed LampArray;
2. persist the ownership choice in the service configuration;
3. gate every OpenBlade keyboard-lighting write at the service boundary;
4. exclude lighting from automatic power/profile transitions while delegated;
5. leave the current visible state unchanged when ownership changes and route
   the user to Windows Dynamic Lighting settings for brightness and effects.

The unpackaged background probe reported `IsAvailable=false`. Treat that as the
current integration boundary, not as device absence. Signed identity
registration is now authored and transactionally integrated with setup, but
live registration, background priority, one bounded static-color apply,
ownership-loss behavior, and restoration all remain to be validated before the
capability can become `ProductionAdmitted`.

The shared capability contract now carries `LampArrayLightingAccess`. PID
`02C6` reports `ObservedOnly`; PID `02E0` and unknown models report no value
because no exact LampArray evidence exists for them. Diagnostics, the Lighting
page, the exact production-admission gate, and firmware-smoke schema 4 all
preserve this state. Strict service IPC advanced from version 28 to 29. This is
a coordinated install boundary: do not live-patch only the tray or Session
Agent across it.

The Session Agent now contains the production read-only observation boundary.
It starts only when the service reports a verified RZ09-0528, the complete
tested BIOS `2.02` / EC `1.09` / MCU `1.9.0.0` tuple, and explicit
`ObservedOnly` or `ReadWrite` LampArray access. The Windows source:

- performs a ten-second-bounded enumeration every 30 seconds while eligible
  and retries failures after five seconds;
- filters exact VID/PID in memory and never reports `DeviceInformation.Id`;
- recomputes the captured geometry-and-virtual-key fingerprint;
- treats zero, duplicate, or fingerprint-mismatched exact devices as
  non-controllable observations;
- publishes only a sanitized state summary when the observation changes.

The observer interface deliberately contains only `ObserveAsync`; it cannot
request priority, set colors or brightness, or claim renderer ownership. Keep
that fail-closed boundary until signed package identity and the reversible
write/restoration matrix are complete.

On 2026-07-29, a temporary focused test executed this production source against
the connected laptop. It found exactly one exact device, reproduced the
approved fingerprint, observed connected/enabled state, and independently
confirmed `IsAvailable=false`. The temporary test source was removed after the
run; no device identifier or unsanitized output was retained.

### Sparse package identity authoring

The signed release pipeline now creates the current-user identity needed for
background Dynamic Lighting without replacing the existing executable/WiX
installation model:

- package identity: `OSSBlade.OpenBlade.DynamicLighting`;
- application ID: `SessionAgent`;
- external executable: `OpenBlade.SessionAgent.exe`;
- runtime/trust: `packagedClassicApp`, `mediumIL`, and the required
  `runFullTrust` capability;
- app extension: `com.microsoft.windows.lighting`, with the package-owned
  `public` folder;
- external-content declaration:
  `uap10:AllowExternalContent=true`.

`Publish.ps1` resolves the signing certificate first and uses its exact subject
for both the sparse-package Publisher and the Session Agent's embedded
`urn:schemas-microsoft-com:msix.v1` claim. A signed build fails if the
certificate is missing, expired, lacks a private key or Code Signing EKU, or
if MakeAppx/SignTool fails. The product's three-part version is converted to a
four-part MSIX version. Because the executable is intentionally supplied by
the registered external location rather than stored inside the package,
MakeAppx pack/unpack uses `/nv`; checked-in tests independently validate every
identity, extension, external-content, visual-asset, and descriptor field.

The production payload contains either both
`OpenBlade.DynamicLighting.msix` and
`OpenBlade.DynamicLighting.identity.json`, or neither. Payload validation
rejects a partial pair, malformed descriptor, or Publisher/name/application
mismatch. Unsigned developer builds deliberately contain neither artifact and
compile the Session Agent without an MSIX identity claim, so they cannot
silently impersonate the background-lighting package.

The Session Agent project copies its selected manifest source to one effective
intermediate manifest and force-touches that input before every compile. This
is intentional: without it, an identity-enabled apphost in the shared `obj`
tree can survive a later unsigned publish because the older baseline manifest
does not make the SDK's incremental compiler run. The authoring test publishes
identity-enabled and then unsigned executables through the same `obj` tree and
extracts both PE manifests; the second executable must contain no
`urn:schemas-microsoft-com:msix.v1` element.

Windows PowerShell 5.1 and PowerShell 7 authoring tests both completed a real
unsigned MakeAppx pack/unpack cycle. A separate temporary publish embedded a
test Publisher in the Session Agent; `mt.exe` extracted and confirmed the
expected package name and application ID.

### Sparse package setup lifecycle

Setup now owns the per-user sparse-package registration as part of the same
transaction as the external executable tree. The Setup project targets Windows
11 build 26100 APIs and uses `PackageManager.AddPackageByUriAsync` with
`AddPackageOptions.ExternalLocationUri` pointing at the exact OpenBlade install
directory. Do not replace this with an unscoped `Add-AppxPackage` subprocess.

Before setup stages any files, it validates the optional package/descriptor
pair through the same strict reader used at registration time. Validation
requires:

- exactly the six reviewed descriptor properties and schema version 1;
- the fixed package name, application ID, package filename, and external
  location marker;
- a nonempty Publisher that exactly matches `AppxManifest.xml`;
- one bounded manifest parsed with DTD processing prohibited;
- the reviewed Session Agent executable, external-content declaration,
  Dynamic Lighting app extension, public folder, and `runFullTrust`
  capability.

A partial, malformed, oversized, or mismatched pair fails while the payload is
still only a source directory. No installed shortcut, service, process,
registration, or binary tree has changed at that point.

The install/update order is deliberate:

1. capture whether the exact package name **and descriptor Publisher** are
   registered for the installing user's SID;
2. stop the installed Session Agent;
3. remove only package full names returned by that exact
   user/name/Publisher query;
4. atomically activate the new external executable tree;
5. if the new tree has a validated pair, register it with the exact install
   directory as its external location and independently query that it became
   active;
6. restore normal service and startup artifacts, then commit the payload.

This also handles a signed-to-unsigned developer downgrade: the old signed
registration is removed while its descriptor is still present, the new
unsigned tree activates, and registration becomes an intentional no-op because
the new pair is absent.

If any later install step fails, recovery first removes a possibly registered
new identity while the new descriptor is present, rolls the external tree back,
then re-registers the old identity only when lifecycle capture proved it was
previously active. Uninstall removes the identity after the Session Agent stops
and before deleting binaries. If binary removal fails, the old registration is
recreated while the old tree remains available. A failed identity recovery is
preserved alongside the original setup failure rather than being hidden.

Add/remove operations share a linked 30-second bound. Deployment failures
surface only the operation, exception type or HRESULT; package paths, user SIDs,
Publisher text, and Windows deployment error text are not logged. Never broaden
removal to package name alone: a different Publisher is outside OpenBlade's
ownership.

The automated matrix covers absent unsigned pairs, partial pairs, strict JSON
and XML rejection, DTD rejection, exact Publisher scoping, multiple exact
package versions, external-location URI selection, deployment HRESULTs,
timeouts, caller cancellation, install rollback, uninstall recovery, and
signed-pair payload preservation.

The Session Agent also exposes one explicit read-only post-install probe:

```text
OpenBlade.SessionAgent.exe --probe-dynamic-lighting-identity <absolute-new-json-path>
```

Launch the WinExe with a wait-capable parent and inspect its exit code plus the
new report file. It does not acquire normal Session Agent startup ownership or
start any service, display, input, or lighting loop. It succeeds only when the
current process has the exact package name and `SessionAgent` application
identity, exactly one approved RZ09-0528 LampArray is connected and enabled,
and Windows reports the array available to this background process. The
schema-1 report contains only enum states, booleans, the exact-array count, and
a failure type/HRESULT. It omits the AUMID, package family/full name,
Publisher, user SID, device ID, geometry hash, paths, and exception message.
The output path must be absolute, name a pre-existing directory, end in
`.json`, and not already exist.

No package has been signed, registered, or installed during this work. When an
operator is available, live validation must use a trusted release certificate
and the normal transactional setup path. Confirm the exact current-user
registration, Session Agent package identity, `IsAvailable` transition,
repair, signed-to-unsigned removal, uninstall removal, and an induced
post-registration rollback before any LampArray write test. Preserve only
sanitized results; never record package full names, user SIDs, certificate
thumbprints, or local paths.

## Preserve discovered special keys without borrowing PID 02E0 reports

Synapse exposes the M1–M5 shortcut surface on RZ09-0528, but OpenBlade's older
Session Agent decoders (`Rz09_0581DefaultLaptopKeyRawHidInput` and
`Rz09_0581PerformanceShortcutRawHidInput`) remain intentionally scoped to the
RZ09-0581 / PID `02E0` raw-input path. Exact PID `02C6` captures now establish:

- M1: Fn state plus Page Up usage `01 00 4B`;
- M2: Fn state plus Page Down usage `01 00 4E`;
- M3, M4, and M5: `04 0A 03`, `04 0A D3`, and `04 0A D4`;
- Fn+P: Fn state plus `01 00 13`.

The first M1 session is retained as a corrected Page Up negative control.
Operator completion-message typing is excluded. Raw sessions remain private;
the sanitized fixture and annotation are capture commit `29b67f0`.

The production code now classifies the exact `MI_01` raw-input path as either
PID `02E0` or PID `02C6` before interpreting a report. Both models reuse the
shared Fn-layer state machine only after that identity decision. PID `02C6`
uses a separate Fn+P decoder and never enters the older D3 dedicated-
performance-button path: `04 0A D3` is M4 on this laptop. PID `02C6` also does
not inherit the older model's reactive matrix, direct default-key, function-
row reconstruction, or keyboard fallback assumptions.

The capability contract now carries `SpecialKeyBindingsAccess`:

- PID `02E0` production lighting reports `ReadWrite`;
- PID `02C6` exact lighting reports `ObservedOnly`;
- unavailable or unadmitted devices report no access value.

The Laptop page only shows editable M1–M5 bindings for `ReadWrite`. On this
model it instead shows an observed-only information card, preserves any saved
global bindings, and explains that they remain inactive. The Session Agent can
parse PID `02C6` reports, but publishing, Page Up/Page Down suppression, and
Fn+P actions remain gated on `SpecialKeyBindingsAccess == ReadWrite`. The
current exact capability remains `ObservedOnly`, so UI and runtime still fail
closed together.
Diagnostics and firmware smoke retain the access level (schema 3 introduced
it; schema 4 adds LampArray access), and strict service IPC advanced from
version 27 to 28 at that checkpoint.

For future evidence collection, use the exact-device probe:

```text
OpenBlade.Capture probe-rz09-0528-02c6-special-keys [seconds] [output-file]
```

The next step is a live OpenBlade decoder validation while the exact capability
remains gated. The exact capture command now feeds MI_01 reports through the
same public PID 02C6 semantic decoders used by the Session Agent and records
`DecodedSpecialKey`/`DecodedPerformanceShortcut` lines. It never enables
special-key actions or suppresses ordinary keys. Confirm every M1–M5 identity,
ordinary Page Up/Page Down pass-through, Fn+P, and the absence of an M4
performance-shortcut collision. Keep raw output private, commit only a
sanitized semantic summary, and only then advance the access level to
`ReadWrite`.

PID 02C6 M4 needs one additional routing rule: when admitted, its default action
must go through the typed M4 action executor even when no custom binding exists.
Otherwise the exact-model branch correctly avoids PID 02E0's D3
dedicated-performance decoder but silently drops the key. Keep PID 02E0 on its
existing dedicated-button path; the override is exact-model plus M4 only.

The first live production-decoder pass was deliberately left gated and produced
a partial negative control:

- M3, M4, and M5 decoded as their exact typed identities;
- M4 did not collide with the older model's performance-shortcut decoder;
- ordinary Page Up and Page Down still reached Windows;
- two operator-correlated Fn+Page Up, Fn+Page Down, and Fn+P retries exposed
  only `04 0A 00 00` / `04 00 00 00` Fn make/release reports on the target
  MI_01 stream, with no `01 00 4B`, `01 00 4E`, or `01 00 13` report.

The retries ran after an independent exact device-mode query returned `Driver`.
Computer Use then inspected Synapse without saving any assignment: the physical
Fn key was labeled the non-remappable `Razer Hypershift` key, and Page Up's
Hypershift-layer assignment was still `Page Up`. This rules out the two easy
explanations—wrong modifier and a remapped key—but does not explain why the
earlier USBPcap oracle saw the `01` reports while Windows Raw Input did not.
Treat that as a transport/collection discrepancy, not as permission to infer
the missing events. Keep `SpecialKeyBindingsAccess=ObservedOnly`; do not publish
or suppress M1, M2, or Fn+P until the production path observes them
independently.

The Windows transport distinction supplies a concrete next test. USBPcap sees
report-ID-01 keyboard usages `4B`, `4E`, and `13`, but Windows may expose a
keyboard top-level collection as `RIM_TYPEKEYBOARD` rather than returning its
original HID bytes through `RIM_TYPEHID`. OpenBlade now has an exact-PID,
MI_01-only candidate path that reconstructs M1/M2 from extended Page Up/Page
Down scan codes `49`/`51` and Fn+P from scan code `19`, while sharing the
existing Fn and edge-latch state so a platform that exposes both forms cannot
double-fire. The capture probe reports those translated identities, and the
production path remains behind `SpecialKeyBindingsAccess`; this is an
implementation hypothesis until a fresh physical probe observes the events.
Ordinary Page Up/Page Down must continue to pass through when no override is
configured.

The bounded
`probe-rz09-0528-02c6-hid-input-access` diagnostic then tested whether a
shared direct HID reader could replace Raw Input without taking device
ownership. It retained no paths or unique identifiers. All ten exact PID
`02C6` top-level collections were found and seven non-keyboard collections
accepted a shared `GENERIC_READ` handle with identity reverified. Windows
returned access denied for both keyboard collections (`MI_00` and `MI_01`,
usage `0001:0006`) and for the `MI_02` protocol collection. This is useful
negative evidence: an ordinary user-mode HID reader cannot simply open either
keyboard collection to recover the missing report-ID-01 events. Do not read
from an adjacent consumer/vendor collection and assume that endpoint-level
traffic will be routed to it. The next physical isolation retry must still
prove whether removing Synapse restores the Raw Input keyboard events; the
first 60-second isolated attempt received no physical input and is preserved
as a failed zero-input session.

A second 60-second retry used the same elevated isolation window as the GPU
validator: Synapse, Razer Elevation Service, Razer Game Manager Service 3, and
OpenBlade were all inactive. It also received zero Raw Input messages. The
wrapper restored every controller afterward. Preserve both attempts honestly;
neither contains a physical key event, so neither advances nor contradicts the
USBPcap identities and neither permits production actions or suppression.

## Preserve the device-mode negative controls and reversible success

Capture commits `42bdc2d` and `ceb52f0` record three elevated exact-device
attempts without publishing raw output:

- the first failed closed on a competing controller before any query or write;
- after stopping the exact known Razer controller processes, the second read
  `Driver` as its baseline and failed `baselineNotNormal`;
- after core commit `915149f` made the validator symmetric, the third used
  `Driver` as its baseline, applied `Normal`, independently read `Normal`,
  received operator confirmation of expected function-layer behavior, restored
  `Driver`, and independently read the restored baseline.

The validator must use the opposite of the exact read baseline as its candidate
and restore that exact baseline. Never hard-code Normal as the required starting
state: doing so incorrectly rejects a valid Driver-to-Normal-to-Driver
validation. Retain the first two no-write sessions as negative controls.

The successful wrapper finished with no manual recovery required and with
OpenBlade, Razer Elevation Service, and Razer Game Manager Service 3 running.
The sanitized record contains only byte lengths, SHA-256 provenance, typed
results, and service states. It contains no raw output, device path, serial
number, or unique USB identifier.

Both exact setters advanced from `SetterValidated` to `ProductionAdmitted` in
capture commit `6aaf65b` after the ownership-aware production lifecycle landed
in core commit `3707c49`. The validation command still reports the capture-time
fact `productionAdmitted=false`; do not rewrite raw provenance to imply that
the validator itself was a production API.

## Treat same-version Burn replacement as an ownership transaction

A live same-public-version replacement exposed a bundle/payload ownership bug.
The new WiX bundle detected `InstalledVersion >= 0.1.0`, skipped its
`OpenBladeSetup` package, then removed the related old bundle. The old bundle's
`uninstall --bundle-managed` removed the service and tray while the new bundle
still registered success. Exit code 0 and an Apps entry were therefore not
sufficient installation evidence.

Core commit `eaee684` closes both halves of the race:

- `OpenBladeSetup` is considered already present only when the installed
  version is strictly greater than the incoming version, so an equal-version
  developer build executes its payload installer;
- bundle-managed installation records the commit-aware product identity as
  `InstalledBuild`;
- a cached older setup executable compares that marker with its own identity
  and returns success without mutation when Burn invokes its late related-
  bundle uninstall.

The first transition from a pre-fix bundle must be a clean uninstall followed
by a clean install, because the old cached setup executable cannot contain the
new supersession guard. After that boundary, validate an actual equal-version
replacement and require all of the following: the new bundle log plans an
`OpenBladeSetup` install, the old bundle's uninstall is a no-op, the ownership
marker matches the new commit, the service is installed and running, and the
tray/session processes use that same product version.

That validation completed from `0.1.0+e8b4d5a0` to
`0.1.0+ca45ac9a`. The installer exited 0, the marker advanced to
`ca45ac9a`, the installed tray reported the full `ca45ac9` product identity,
and the OpenBlade service remained Running/Automatic. The final installer
SHA-256 was
`3A7A78C17368D000CE638AC1A2CC5204975FCF1B4873E43883B93AD4E04ED2E0`.

Always spell the Store and unattended command as
`/install /quiet /norestart`. Omitting `/install` leaves the requested Burn
action ambiguous and is no longer part of the submission contract.

## Keep user-facing identity and firmware smoke model-specific

Do not use `TargetBladeDevice` for generic service, diagnostics, tray, or
firmware-smoke presentation. That type intentionally describes the older
RZ09-0581 / PID `02E0` protocol target. The shared
`RazerBladeModelCatalog` now carries the canonical USB VID/PID for both
supported models:

- RZ09-0581 -> `1532:02E0`;
- RZ09-0528 -> `1532:02C6`.

Diagnostics derive `device.json` from the verified firmware model, About binds
the USB row dynamically, and an unknown model is reported as unavailable
instead of being mislabeled. `TestedPlatformCatalog` includes the validated
RZ09-0528 BIOS `2.02`, EC `1.09`, and MCU `1.9.0.0` tuple.

Firmware-smoke schema 4 resolves the target from the service observation and
uses an exact capability gate per model. RZ09-0528 expects four AC performance
presets, four fan modes, keyboard Off, seven observed quick effects, all three
logo modes, three admitted laptop controls, observed-only special keys, and
observed-only LampArray access.
Reversible and recovery confirmation tokens are derived from the baseline
model (`RZ09-0528-MCU-SMOKE` here), and recovery additionally proves the live
device model matches the baseline before any setter can run.

## Final exact-model capability-integrity checkpoint

The read-only `probe-rz09-0528-02c6-production-admission` success gate must
cover both admitted controls and observed-only discoveries. It now rejects a
build if any of these exact-model facts disappears or changes:

- four AC performance presets and the seven allowed Custom CPU/GPU pairs;
- the 2,000-5,000 RPM fan range, all four fan modes, and all seven charge
  limits;
- CPU Power Limit 45-95 W, CPU Voltage Optimizer -30 to -1, and the GPU offset
  policy as observed-only;
- the complete Battery and USB-C behavior observations;
- keyboard Off, all seven observed quick effects, three logo modes, keyboard
  brightness, observed-only special keys, and observed-only LampArray access;
- startup animation, function-key primary behavior, and Gaming mode.

The gate is independently unit tested to reject dropped discoveries and an
older-model Custom combination assumption.

## Delegate keyboard lighting to Windows; do not become a WDL renderer

The exact 85-lamp LampArray discovery changed the product boundary. Windows
already recognizes this keyboard and supplies Dynamic Lighting. OpenBlade must
therefore offer ownership selection, not a second Windows LampArray renderer.

The persisted `KeyboardLightingControlMode` has two values:

- `OpenBlade`: the existing HID-backed keyboard lighting controls are available;
- `WindowsDynamicLighting`: Windows owns the keyboard and OpenBlade does not
  send keyboard-lighting writes.

Delegation is enforced in the service, not only by disabled UI. It blocks
manual effect requests, brightness hotkeys, display/idle suppression, reactive
key pulses, Ambient Awareness frames, playback-mute indicators, renderer
recovery, and profile/power-transition lighting application. Selecting the
mode itself preserves the current visible lighting; returning to OpenBlade
also performs no immediate write. The next explicit OpenBlade action establishes
new intent.

The tray presents the ownership toggle beside a link to
`ms-settings:personalization-lighting`, disables OpenBlade keyboard controls
while delegated, and explains that Windows owns brightness and effects. The
illuminated lid logo remains a separately labelled firmware endpoint because
it is not part of the observed keyboard LampArray; do not couple a logo-only
action to a hidden keyboard write.

Keep the ownership card's title, description, settings link, and toggle as
direct children of a Grid, but anchor the toggle to the left edge and offset the
title beside it. Both the original vertical StackPanel and a star/auto
two-column Grid allowed the long description to measure wider than the page,
leaving the toggle loaded in the accessibility tree but clipped beyond the
visible window. Commits `addebe7` and `ca45ac9` preserve both failed live
iterations and the final left-edge fix; the shell-contract regression now
rejects a right-column toggle.

The ownership card exposed a broader WinUI measurement trap at compact window
sizes. Disabling horizontal scrolling does not by itself constrain a vertical
`StackPanel` inside a `ScrollViewer`; long card content can still establish an
oversized desired width and be clipped because the horizontal scrollbar is
hidden. Every page root therefore binds its `Width` to its own
`ScrollViewer.ViewportWidth`. Keep that invariant for Dashboard, Profiles,
Laptop, Lighting, Display, Settings, and About. The tray layout contract test
enumerates all seven pages so a future card cannot silently reintroduce
horizontal overflow.

Installed build `0.1.0+4cb95cefc9a6ec0ebbf3fa4a467ce3a034b9da19`
was visually checked at an 828-by-716 window after a real service restart.
All seven pages kept their cards inside the viewport, including the long
Dynamic Lighting and observed-effects notices. The tray, Session Agent, and
service binaries all reported that exact product version. With ownership still
delegated to Windows, the exact-device HID brightness query independently read
raw `128`, decoded as 50%, with no open, query, or protocol failure. The
post-upgrade firmware smoke produced 26 passes, one warning, and two expected
failures: the sleeping NVIDIA GPU temperature was unavailable, while
`RazerAppEngine` and its externally selected Performance mode were deliberately
reported as competing control. Its private diagnostics remain under the
ignored `artifacts/validation` tree and must not be committed.

The earlier signed sparse-package registration and priority probe remains
valuable negative evidence: it proved that an OpenBlade background provider
would compete with Windows' existing controller. Do not ship that experiment
as the production lighting architecture. `build/Publish.ps1` therefore no
longer gives the Session Agent package identity or emits the sparse identity
package. The authoring/probe code remains as developer evidence and the setup
removal path remains so upgrades can clean installations made by the
experiment. Uninstall the temporary validation package/certificates after the
replacement build is installed and validated.

IPC protocol 30 adds the ownership request and status field. The regression
test records the device lighting-write count, delegates to Windows, exercises
effect, brightness, and display-off suppression requests, and proves the count
does not change. It also reloads the machine configuration to prove the choice
persists.

Live exact-device validation completed on `ca45ac9`:

- Synapse Plugged In visibly showed brightness On at 50 percent, Static quick
  effect, and Static logo; exact `03/83 010500` readback returned raw `128`;
- OpenBlade service startup applied the prior OpenBlade-owned Off state before
  delegation, and exact readback returned raw `0` even while Synapse still
  displayed its cached On/50/Static state;
- the left-anchored ownership switch was enabled, OpenBlade reported that
  Windows owned the keyboard, and all OpenBlade keyboard controls disabled;
- Synapse was deliberately toggled Off then On once to reapply Static 50 after
  the ownership boundary; exact readback returned raw `128`;
- restarting only the OpenBlade service left it Running/Automatic and a final
  exact readback again returned raw `128`.

This sequence is the required validation pattern: select delegation first,
then reapply the desired external-controller state once, then independently
read it back across a service restart. Never use the cached Synapse page as
firmware readback. Sanitized capture commit `f19a60d` records the sequence
without a packet capture or unique device identifier.

## Admit PID 02C6 Normal/Driver mode as an owned lifecycle

The reversible exact-device run began in Driver mode, applied Normal, confirmed
Normal through `00/84 0000`, restored Driver, and independently confirmed the
restored baseline. That evidence admits both `00/04 0000` and `00/04 0300`,
but it does not admit the PID 02C6 special-key decoder.

The production port therefore treats Driver mode as an ownership-aware
lifecycle:

- a pre-existing Driver mode is observed and preserved;
- a Normal-to-Driver transition made by OpenBlade is marked as owned before
  submission and independently read back;
- disposal or invalidation restores Normal only when OpenBlade owned the
  transition and the device is still in Driver mode;
- explicit service shutdown may release a pre-existing Driver mode after the
  existing Synapse/conflict checks, again with independent readback;
- invalidating the PID 02C6 port closes its HID actor instead of leaking the
  command session.

Keep `SpecialKeyBindingsAccess=ObservedOnly` until M1-M5, Fn+P, and ordinary
Normal-mode key behavior have all exercised the production decoder. Device-mode
write admission is not evidence that those report mappings are safe.

Final validation at the current transactional-identity checkpoint:

- core commits: `b611f0b` (special-key capability/UI) and `eda8d67`
  (model-derived identity and firmware smoke), followed by `d987a73`
  (exact-model production-admission capability gate) and `cde59dd`
  (sanitized, read-only LampArray geometry probe), `e96b2f2` (production
  read-only Session Agent observer), and `44b06a4` (signed sparse-package
  authoring); `c19fe0c` (`feat(setup): transact background identity`) is the
  lifecycle checkpoint described above;
- documentation commits: `1083340`, `6c3c631`, `c545805`, and public-site
  checkpoint `c38e993`;
- Release build: 0 warnings and 0 errors;
- full Release suite: 3,278 tests passed across all ten projects
  (Display 38, Capture 253, Session 33, Contracts 192, SessionAgent 167,
  Protocol 1,049, Tray 615, Service 685, Setup 227, FirmwareSmoke 19);
- `dotnet format OpenBlade.slnx --verify-no-changes --no-restore`: clean;
- Windows PowerShell 5.1 sparse-package identity, publish-payload, and
  installer-authoring suites: all passed;
- Session Agent identity-probe matrix: exact/missing/mismatched package and
  application identity, approved/unavailable array, redaction, failure
  HRESULT, invalid output, and no-overwrite cases passed;
- Windows PowerShell 5.1 capture toolkit: all five regression suites passed
  with `-NonInteractive`;
- public documentation: all local links and assets resolved across 11 pages;
- post-`c19fe0c` non-mutating production probe:
  `ReadyWithAccessFallback`, exact
  4/4/7 battery capability counts, seven observed lighting effects,
  `SpecialKeyBindingsAccess=ObservedOnly`, all three observed CPU/GPU ranges,
  `LampArrayLightingAccess=ObservedOnly`, two power-source observations,
  brightness raw 0, idle fan RPM 0/0, startup
  enabled, MCU `1.9.0.0`, full-power 280 W AC, Function Keys primary, Gaming
  mode off, Driver mode, logo Static, no open/query failures, and
  `Success=True`.

Update this section after every evidence, implementation, or documentation
checkpoint.

## Treat the Razer EC thermal-zone path as model-specific

The first exact-device firmware smoke reported CPU temperature unavailable even
though Windows exposed the Razer EC thermal zone. A read-only PDH enumeration
showed why:

- RZ09-0528 BIOS 2.02 / EC 1.09 publishes TZRZ as
  `\_SB.PCI0.SBRG.EC0.TZRZ`; its sampled high-precision value `3182`
  converted to 45°C;
- the earlier RZ09-0581 implementation queried only
  `\_SB.PC00.LPCB.EC0.TZRZ`;
- Windows also exposed `\_TZ.TZ01`, but OpenBlade must not silently substitute
  an unrelated thermal zone.

Keep both validated TZRZ counter paths as explicit candidates, newest exact
model first. Do not replace them with a wildcard or “first thermal zone”
selection. The conversion remains tenths Kelvin to Celsius with the existing
0–115°C plausibility bound.

Installed service build `0.1.0+3449fc71` independently published `45°C` after
upgrade, and the upgraded tray displayed the same value. Capture revision
`8c92256` records the sanitized read-only counter discovery, live service/tray
validation, privacy boundary, and a Windows PowerShell 5.1 regression test.
`sensors.cpuTemperature` is therefore `ProductionAdmitted`.

## Remaining exact-device completion frontier

Do not interpret the green admission probe as full RZ09-0528 feature coverage.
The sanitized schema-2 coverage ledger is the completion source of truth. At the
`32073d8` service-restart-admission checkpoint its 144 leaves contain:

- 53 `ProductionAdmitted`;
- 3 `QueryValidated`;
- 30 `Captured`;
- 1 `SetterValidated`;
- 10 `SoftwareOnly`;
- 3 `Absent`;
- 44 `NotInvestigated`.

`Rz090528CoverageContractTests` pins both those counts and every
`Captured`/`QueryValidated`/`SetterValidated` path. Advancing evidence must
deliberately update that checkpoint and the exact-model capability gate. This
prevents a new model-specific discovery from disappearing merely because the
current admitted subset still builds.

The main physical-evidence groups still blocking broader admission are:

- independent USB-C and battery performance/readback, including an exact PID
  `02C6` adapter-class response and thermal readback;
- CPU Power Limit, CPU Voltage Optimizer, and GPU overclock setters/readback
  through a lifecycle OpenBlade can own without Synapse;
- quick-effect parameters and effect getter for OpenBlade's native HID path;
  the model's 51-byte collection is query-validated as an 85-lamp Windows
  LampArray, but OpenBlade delegates to Windows instead of registering a
  competing background renderer;
- exact Normal-mode special-key parity;
- production validation of the PID `02C6` special-key decoder. M1-M5 and Fn+P
  reports are captured, including the critical M4 `040AD300` collision with
  the older PID `02E0` performance-key assumption, but remain observed-only;
- cold boot, sign-in, sleep/resume, and device reconnect.

The next agent should rerun the coverage-contract test before and after changing
the ledger. A failing frontier is a prompt to represent or admit the new fact,
not to weaken the expected set.
