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
| Model-specific tuning UI | Observed-only | CPU Power Limit 45-95 W in 1 W steps, CPU Voltage Optimizer -30..-1, and Synapse GPU policy ranges are first-class capabilities. Synapse's CPU backend is identified as RzAMDOverClock/RzDLLService but is not admitted because its refresh lifecycle can write; OpenBlade shows these features without editable controls |
| Power-source matrix | Captured, not admitted | Nine exact-device sessions cover barrel AC, USB-C-only, battery, both quiet baselines, and all directed transitions; exact PID 02C6 USB-C reply and independent thermal readback remain missing |
| Battery charge limit | Admitted | Query, 50/55/60/65/70/75/80-percent setters, protection off, readback, and restoration |
| Temporary full charge | Admitted | Query, enable, cancel, readback, and restoration |
| Keyboard behavior and special keys | Partially admitted | Function-key primary, Gaming mode, and startup-animation setters; device mode read-only; special keys pending |
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

The first successful AC-to-USB-C session physically passed through a cable-free
battery interval. Its `On Battery` UI was not delayed USB-C recognition. The
session originally labeled as a USB-C-to-AC restoration was a no-action stable
USB-C baseline; retain that correction in every downstream interpretation.

USBPcap decoded outgoing feature reports only. It did not capture an exact PID
`02C6` USB-C `07/8C` reply or an independent `0D/82` destination-state
readback. Therefore the matrix advances those coverage leaves to `Captured`,
not `QueryValidated`, `SetterValidated`, or `ProductionAdmitted`. Do not copy
the generic PID `02E0` `0711` USB-C mapping into this device. The next agent
must obtain direct exact-device readback while physically on USB-C and battery
before admitting source-specific setters.

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
   first three setters admitted; device mode remains query-only
6. Special-key report mapping and default-action preservation
7. Windows display modes, DRR, HDR, and color profiles
8. Conflict, timeout, malformed-response, disconnect, and lifecycle validation
9. Cold boot, sign-in, service restart, sleep/resume, and reconnect validation
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
- These model-independent Windows display paths are production-admitted for
  RZ09-0528. HDR-mode color profiles were not exercised.

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

Update this section after every evidence, implementation, or documentation
checkpoint.
