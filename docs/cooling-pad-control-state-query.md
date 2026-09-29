# Cooling-pad control-state query

Tracking: [issue #119](https://github.com/OpenBladeProject/openblade-core/issues/119).
Evidence: [captures PR #33](https://github.com/OpenBladeProject/openblade-captures/pull/33).

This developer-only command sends one captured `00/84:0000` query to the
revision-0200 `1532:0F43` cooling pad on RZ09-05289EN4. It accepts only the
captured two-byte raw results `0000` or `0300`. Neither result is assigned
laptop Driver/Normal semantics, fan mode, RPM, or ownership meaning.

The actual MI_00 report descriptor was captured during attended USB reconnect
on 2026-09-29. Its SHA-256 is
`C86119101B32403BB8C8EFBDEB67654B348C7C28E298B711D9FE1DB8C129DAFF`.
It declares one unnumbered 90-byte constant Feature report. Windows metadata
reports 91 feature-buffer bytes; Windows requires prefix zero for an
unnumbered report. There is no fallback to historical prefix `05`. Descriptor
geometry is established; actual OpenBlade feature I/O remains unvalidated.
BIOS versions do not restrict this query. Output `device.biosVersion` identifies
the captured-evidence BIOS (2.02), not the runtime host BIOS;
`deviceBiosIsCaptureProvenance` is true. Record the runtime BIOS separately in
the metadata inventory for any trial.

## Attended trial

1. Save operator-confirmed Auto, full-brightness static-green lighting and
   normal physical fan state. Confirm pad-dependent HyperBoost is inactive.
2. Verify USBPcap has stopped. The command also blocks while USBPcapCMD is
   detected and fails closed if capture-process detection fails.
3. Establish competing-controller absence manually. Synapse AppEngine and
   detected vendor services block the query. Laptop product exclusions in
   patched Synapse do not authorize accessory access. Closing a window or
   exiting the tray application may leave services running. Do not kill vendor
   processes or automatically stop services to pass this gate.
4. From the exact reviewed build, run:

   ```powershell
   dotnet run --project src/OpenBlade.Capture --no-build -- query-rz09-0528-cooling-pad-control-state
   ```

   It accepts no report ID, payload, alternate command or target argument.
   Exact host/SKU, pad VID/PID/revision, usage `000C:0001`, 91-byte capability
   geometry and a sole matching handle are checked before access. Controller
   checks are repeated before submission. The exchange uses the existing
   cancellable three-second transport timeout and bounded cleanup.
5. Record the raw result and transport validation separately from physical
   observation. The shared session validates returned report prefix, body
   length, checksum, transaction, command, status and captured payload shape.
   On ambiguity, stop without retry or a setter. This command does not claim
   production admission or meaningful fan-state readback.
6. Reopen Synapse normally and confirm the saved Auto/lighting state and
   normal physical behavior. Any unexpected state belongs in the trial
   evidence; use vendor recovery instead of replaying commands.

The command performs no mode change, fan setter, lighting write, ownership
acquisition or restoration command. Production accessory mutation stays closed
until independently reviewed restoration and lifecycle contracts exist.
