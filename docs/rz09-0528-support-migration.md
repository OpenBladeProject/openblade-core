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

Keyboard brightness is the only admitted hardware control for this model.

| Area | Status | Production scope |
| --- | --- | --- |
| Exact host and HID identity | Admitted | Model, BIOS, PID, usage `0001:0002`, and 91-byte feature reports |
| Keyboard-brightness query | Admitted | `03/83`, arguments `010500` |
| Keyboard-brightness setter | Admitted | `03/03`, arguments `0105xx` |
| Lighting effects and logo | Unavailable | No PID `02C6` production writes |
| Performance and fans | Unavailable | No PID `02C6` production writes |
| Battery settings | Unavailable | Read-only factory only |
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

Update this section after every evidence, implementation, or documentation
checkpoint.
