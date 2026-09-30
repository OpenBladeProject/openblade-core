# Cooling-pad USB reconnect while Fixed is active

Tracking: [issue #119](https://github.com/OpenBladeProject/openblade-core/issues/119).
Normal process exit and developer idle-wait cancellation each restored Auto in
one attended trial. This next experiment isolates USB re-enumeration while
Medium remains selected, after orderly native disposal and process exit.

## Fixed boundary and conditional restoration

The live supervisor retains the present operator-confirmed quiet Auto/full-green
baseline and original raw `0000`. Child 1 uses captured transactions 1–4 to
query `0000`, acquire/query `0300` and acknowledge Medium `01012C`. Require fresh
physical Medium/full-brightness static-green/no-red-or-sleep confirmation,
completed native disposal with its receipt and actual normal exit code zero.

Only then instruct the present operator to unplug USB while retaining 12 V.
Passively sample pad USB absence for at least five seconds, with bounded
waits, before requesting reconnection to the same port. Premature reconnection,
observation failure, timeout or supervisor cancellation stops continuation.
After reconnect, obtain a fresh physical check that Medium/full-green remains
normal. USB presence/absence observations do not prove retained 12 V or physical
pad identity; those remain contemporaneous operator provenance. The nominal 250 ms polling interval cannot exclude a short reconnect between samples.

Launch authenticated child 2 once. It freshly admits the sole exact pad/interface,
model/SKU and controller absence. Its first command, transaction 5, is the
read-only control-state query. Only observed raw `0300` permits captured Auto
transaction 6, physical quiet-baseline confirmation, original raw `0000`
restoration/query transactions 7–8 and the final physical check before disposal.
Raw `0000`, another value, failed query, changed/ambiguous device identity or
abnormal physical behavior stops all later mutations. Never reacquire `0300`
to manufacture the expected baseline.

## Admission and evidence

Use a separate exact trial identity and retain continuation authority only in
the live current-user pipe authenticated against native process identity and
loaded/disk build. Preserve the earlier normal-exit and cooperative-cancellation
rules. BIOS versions remain provenance; gate on exact model/SKU and pad identity.
No retry, force-kill, saved continuation file or unconditional firmware cleanup
is included. Late callbacks cannot launch another process or resume writes.

Before live use, commit/rebuild exact source, record hashes, pass focused tests
and actual hardware-free subprocess checks, and obtain independent review. Then
obtain the present quiet-Auto/full-green baseline with HyperBoost inactive.
Synapse, Razer services and USBPcap remain stopped. No lighting/demand command
is included. Manual power cycling is the attended contingency on ambiguity,
without guaranteed preferred-setting restoration or vendor recovery.

Record observed USB absence duration, presence, raw query and accepted physical
gates separately from operator power/port provenance. Publish sanitized stage
results and hashes, excluding PIDs, nonces, serials, paths and raw reports. Typed
output and operator observations are not independent native packet tracing.

Success would establish one orderly-close USB-only reconnect while Fixed is
active. Active-handle disconnection, forced process loss, native-I/O cancellation,
power loss, sleep, independent fan-state/RPM, ownership and production handback
remain unvalidated.

## Attended command

```powershell
OpenBlade.Capture.exe validate-rz09-0528-cooling-pad-fan-fixed-usb-reconnect `
  --confirm-target RZ09-05289EN4:1532:0F43:0200:Medium2200-NormalExit-UsbOnlyReconnect5s-Auto `
  --confirm-recovery ManualPowerCycleOnly
```

The operator gates are baseline, Medium, USB-only disconnect, same-pad/same-port
reconnect, post-reconnect Medium, restored Auto and final state. Keep USB unplugged
until the distinct reconnect instruction; the supervisor samples absence first.
Only the supervisor reads these replies. Each observation and physical wait is
bounded; late results cannot authorize continuation.

Run `selftest-cooling-pad-fan-fixed-usb-reconnect` for simulated actors and USB
observations in actual subprocesses. Preparation checks passed 697 Capture tests,
three architecture checks and 24 hardware-free USB subprocess scenarios plus
direct-child rejection. The cooperative 19 and normal-exit 18 subprocess scenarios plus direct invocation also passed. These checks accessed no pad. Independent source/tests/documentation review passed;
exact committed-source rebuild and host checks precede the physical baseline.
No Fixed USB-reconnect trial is armed or completed.
