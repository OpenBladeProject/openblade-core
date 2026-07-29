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

Keyboard brightness, battery limit 70, and temporary full charge are the
admitted hardware controls for this model.

| Area | Status | Production scope |
| --- | --- | --- |
| Exact host and HID identity | Admitted | Model, BIOS, PID, usage `0001:0002`, and 91-byte feature reports |
| Keyboard-brightness query | Admitted | `03/83`, arguments `010500` |
| Keyboard-brightness setter | Admitted | `03/03`, arguments `0105xx` |
| Lighting effects and logo | Unavailable | No PID `02C6` production writes |
| Performance and fans | Unavailable | No PID `02C6` production writes |
| Battery charge limit | Partially admitted | Query plus captured 70-percent setter only |
| Temporary full charge | Admitted | Query, enable, cancel, readback, and restoration |
| Other battery settings | Unavailable | Other limits and protection-off are not admitted |
| Keyboard behavior and special keys | Unavailable | No PID `02C6` production admission |

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
- `decoded/rz09-0528-pid-02c6-bios-2.02-evidence.json`
- `decoded/rz09-0528-pid-02c6-bios-2.02-device-coverage.json`

The exact-device facts are:

- one 80-to-70 Synapse capture produced `07/12` arguments `C6`;
- `07/92 00` returned `C6` at 70 percent and `D0` at 80 percent three
  times each;
- OpenBlade completed reversible 80-to-70-to-80 and 80-to-75-to-80
  validations in a quiet-controller window;
- with Synapse still running but keyboard lighting switched off by operator
  request, ten queries and two more reversible validations succeeded;
- the exact production factory opened and returned confirmed 80-percent state
  three times while exposing only charge limit 70;
- the final firmware and visible Synapse state was restored to 80 percent, and
  keyboard lighting was intentionally left off.

Only 70 percent is production-admitted. The 75-percent setter was reversibly
validated but not independently captured. The other limits and protection-off
remain unavailable.

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

Before interpreting a query failure, check whether host-rendered Synapse
lighting is continuously issuing `03/0B` traffic. Preserve the failure as
contention evidence. Do not treat a successful quiet-window query as proof that
concurrent traffic is safe.

## Remaining full-support matrix

Work through read/query-backed controls before one-shot setters.

1. Battery charge limit and temporary full charge
2. Performance presets for AC, USB-C, and battery power
3. Automatic, fixed, maximum, and manual-curve fan control plus RPM readback
4. Keyboard lighting effects, matrix geometry, cadence, effect getter, and
   logo modes
5. Function-key primary mode, Gaming mode, startup animation, and device mode
6. Special-key report mapping and default-action preservation
7. Windows display modes, DRR, HDR, and color profiles
8. Conflict, timeout, malformed-response, disconnect, and lifecycle validation
9. Cold boot, sign-in, service restart, sleep/resume, and reconnect validation
10. AC, USB-C, and battery transition validation

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
  -NoProfile -ExecutionPolicy Bypass -File .\tests\Run-All.ps1
```

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

Update this section after every evidence, implementation, or documentation
checkpoint.
