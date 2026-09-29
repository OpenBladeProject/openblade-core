# Cooling-pad raw control-state round trip

Tracking: [issue #119](https://github.com/OpenBladeProject/openblade-core/issues/119).
Preparation: [draft PR #624](https://github.com/OpenBladeProject/openblade-core/pull/624).
Evidence: [captures PR #33](https://github.com/OpenBladeProject/openblade-captures/pull/33).

This developer-only trial tests the captured mode-family values without assigning
laptop Driver/Normal, fan mode, RPM, ownership or safe-handback semantics. It is
separate from the failed historical standalone `0D/10:000600` release. No fan,
lighting, demand or laptop command is included. Production mutation stays closed.

## Evidence and scope

The Auto and Fixed vendor exit/reopen captures independently contain `00/04`
setters with two-byte payloads `0000` on exit and `0300` on startup. Responses
are status `02`, matching transaction/command/checksum and exact payload echo.
Startup `00/84:0000` queries return `0300`. Startup also includes initialization,
fan and lighting traffic; its full behavior is not attributed to the mode setter.

The descriptor is an unnumbered 90-byte Feature report. The prior single
OpenBlade query validates Windows prefix zero with a 91-byte buffer and raw
response `0000`. This supports a bounded mode-only experiment, not a proven
independent restoration or production release contract. No vendor capture queried
raw `0000` immediately after the exit setter; that final readback is part of this
new validation.

The exact admitted developer target is host RZ09-05289EN4 and pad `1532:0F43`
revision `0200`, usage `000C:0001`, with 91 feature-buffer bytes. BIOS is recorded
as provenance and never gates this command. Strict controller guards retain
Synapse AppEngine, active Razer services and USBPcapCMD as conflicts; detection
errors fail closed. No vendor process or service is stopped by the command.

## Fixed sequence and stop conditions

1. Query `00/84:0000` and save the raw baseline. Only a confirmed `0000` permits
   the next step. A baseline `0300`, unknown payload or failed query sends no setter.
2. Submit `00/04:0300` once. Require status `02` and exact `0300` echo.
3. Query `00/84:0000` once and require raw `0300`.
4. Immediately submit `00/04:0000` once and require its exact acknowledged echo.
5. Query `00/84:0000` once and require raw `0000`.

There is no dwell, repeated demand, arbitrary payload, alternate report ID or
retry. Exact identity is checked when opening the sole retained handle. Each
submission rechecks controller absence and has a three-second exchange bound.
The restoration setter and final query share a five-second budget; disposal has
a separate five-second bound. Once candidate readback is confirmed, ordinary cancellation may still allow the one bounded
restoration and final readback. Failed or ambiguous submission/readback stops
further writes; it never triggers an unconditional restoration setter. If any
apply was attempted without confirmed restoration, report manual recovery needed.

A complete raw round trip does not establish fan or lighting restoration,
firmware ownership or safe handback. Physical confirmation is recorded separately.
Unexpected physical behavior requires an attended recovery decision, not a
command replay, fallback ID, guessed Auto release or automatic vendor restart.

## Attended execution checklist

Before execution, record the exact reviewed source/build hashes and a fresh
metadata/process/service inventory privately. Confirm the operator is present,
the pad is still behaving normally in the preferred Auto/full-brightness
static-green state, pad-dependent HyperBoost is inactive, and USB and 12 V power
remain connected. USBPcap must be stopped throughout OpenBlade access.

Show the exact five-step sequence above and obtain fresh approval for this first
OpenBlade mode write. The previous query approval did not validate setters.
The current user explicitly requested leaving Synapse and the two stopped Razer
services stopped. Preserve that state; this trial does not restart them.

Run only the exact reviewed build with:

```powershell
dotnet run --project src/OpenBlade.Capture --no-build -- validate-rz09-0528-cooling-pad-control-state-round-trip --confirm-target RZ09-05289EN4:1532:0F43:0200:0000-0300-0000
```

Save the typed output, exit code and timestamps privately. Confirm the physical
fan/lighting outcome after completion, including any flashing-red or sleep
indication. Publish only a sanitized annotation with artifact hash/length,
confirmed raw values, attempt/acknowledgement/restoration flags, operator outcome
and limitations. Do not claim independent packet capture when none ran.

## Validation status

The single attended trial completed on 2026-09-29 from reviewed source
`746586c9`. Its fixed sequence confirmed raw `0000` -> `0300` -> `0000`, with
both exact setter echoes, all three readbacks and exit code zero. No retry,
fan/lighting setter, fallback report ID or vendor restart occurred.

The operator confirmed normal unchanged fan and full-brightness static-green
lighting, with no flashing-red or sleep indication. Synapse and the two Razer
services remain stopped at the user's request. The sanitized typed-result record
is `2026-09-29-rz09-0528-cooling-pad-control-state-round-trip.json` in captures
PR #33. No concurrent PCAP or native report trace was retained.

This validates immediate restoration of the saved raw value on this exact device.
Meaningful fan state, ownership, fan/lighting handback and production mutation
remain unvalidated. Any further hardware action needs its own reviewed contract.
