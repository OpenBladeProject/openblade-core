# PID 02E0 capture workflow

Each production hardware write remains disabled until its exact command has
passed all five gates: captured, decoded, applied interactively, read back, and
restored.

Current production admission for this exact device includes battery limits
50–80%, protection off, temporary-full-charge readback, and the paired
Charge-to-100 enable/cancel commands. The temporary override is exposed only
when both setter directions and readback are trusted; performance, fan-mode,
and lighting writes remain unadmitted.

## Prerequisites

- Target: Razer Blade 16 `RZ09-0581`, `1532:02E0`, BIOS `3.01`.
- Synapse `4.0.695` may remain installed for other devices and serves as a
  protocol oracle during capture. Stop its controllers temporarily only for a
  strict one-time write-validation cycle; released OpenBlade operation does not
  require Synapse to be disabled.
- Wireshark and USBPcap are installed. Reboot Windows before capture so the USB filter becomes active.

## Procedure

1. Close unrelated Chroma applications and set keyboard lighting Off to reduce traffic.
2. Start USBPcap on the root hub containing `VID_1532&PID_02E0`, restricted to that device address and capturing full packets with descriptors. Always use `captures/tools/Start-UsbPcapCapture.ps1`; it creates an isolated Windows process group and an ownership session used for safe shutdown. Do not substitute `Start-Process`, which cannot request `CREATE_NEW_PROCESS_GROUP`.
3. Change exactly one Synapse setting, wait for its response, and annotate the timestamp and action.
4. Restore the previous value before moving to the next action.
5. Stop USBPcap with `captures/tools/Stop-UsbPcapCapture.ps1 -SessionPath <session.json>` so the PCAP is flushed and the root handle is released. The helper attaches a disposable worker to the owned capture console and sends `CTRL_BREAK_EVENT` only to the nonzero USBPcap process-group ID. It never broadcasts `CTRL_C_EVENT` and never detaches the calling PowerShell or Codex process.
6. Export only the matching feature-report request and response bytes. Do not commit raw PCAP files, serial numbers, or unrelated USB traffic.
7. Create a sanitized fixture entry with the exact device identity and all validation flags initially false except `captured` and `decoded`.
8. Run `OpenBlade.Capture validate fixtures.json`. It must fail until interactive application, readback, and restoration have each been confirmed. After review, add the exact fixture-file SHA-256 to the code-controlled production registry; fixture metadata alone cannot authorize commands.

For example, prepare the existing USBPcap arguments and keep the returned
ownership session next to the temporary capture metadata:

```powershell
$pcap = 'C:\tmp\openblade-validation.pcap'
$session = 'C:\tmp\openblade-validation.usbpcap.json'
$capture = & .\captures\tools\Start-UsbPcapCapture.ps1 `
    -ExecutablePath 'C:\Program Files\USBPcap\USBPcapCMD.exe' `
    -ArgumentList @('-d', '\\.\USBPcap3', '--devices', '2', '--inject-descriptors', '-s', '65535', '-o', $pcap) `
    -OutputPath $pcap `
    -SessionPath $session

& .\captures\tools\Stop-UsbPcapCapture.ps1 -SessionPath $capture.SessionPath
```

Graceful shutdown is the default and must be tried first. If targeted
`CTRL_BREAK_EVENT` cannot stop the owned process, the helper fails without
killing it. `-ForceFallback` is an explicit last resort; it emits a warning and
uses `Stop-Process -Force`, which may lose buffered capture data and leave the
PCAP incomplete. Never use the former helper implementation or any direct
`GenerateConsoleCtrlEvent` call with a zero process-group ID.

## Spectrum and Random Breathing oracle capture

The reviewed long-running oracle plan is
`captures/plans/2026-07-18-lighting-spectrum-random-oracle.json`. It contains
three independent 100-second Spectrum runs, separated by 10-second
Static-green boundaries, followed by one uninterrupted 1,800-second Random
Breathing run. The 100-second windows seek at least two complete Spectrum wraps
but deliberately encode no expected period: the existing partial arcs do not
prove a wrap or global cycle duration. Including the initial and final evidence
boundaries and restoration, the controlled observation time is 2,160 seconds
(36 minutes). The runner uses a 2,700-second (45-minute) timeout to leave nine
minutes for deliberate Synapse UI transitions.

Do not start this capture from Codex without first showing the current launch
and shutdown implementation to the user and receiving explicit approval. From
an already elevated PowerShell, the approved command is:

```powershell
& .\captures\tools\Invoke-LightingSpectrumRandomCapture.ps1 `
    -UsbPcapDevice '\\.\USBPcap3' `
    -DeviceAddress 2 `
    -OutputDirectory 'C:\tmp\openblade-lighting-spectrum-random-oracle-v1'
```

Re-enumerate the root and device address immediately before using the command;
the example address is not stable across reconnects or reboots. The
orchestrator never calls a console-control API or the USBPcap helpers directly.
It starts only `Invoke-InteractiveUsbPcapCapture.ps1`, which in turn launches
USBPcap as the root of a nonzero `CREATE_NEW_PROCESS_GROUP` and stops it through
the isolated worker's targeted `CTRL_BREAK_EVENT`. The workflow does not enable
the force-stop fallback.

For every step, the orchestrator records UTC timestamps immediately before the
Synapse action and immediately after the visual change is confirmed. It then
holds the reviewed window without further lighting changes. The resulting
annotation JSON records the raw PCAP hash and size only after graceful shutdown
and a successful one-packet tshark decode. Follow the plan's annotation
guidance when producing sanitized fixtures; never commit the raw PCAP or treat
this Synapse-oracle session as OpenBlade installed-build validation.

After a graceful capture, generate the bounded, sanitized measurement artifact
with:

```powershell
& .\captures\tools\Analyze-LightingSpectrumRandomOracle.ps1 `
    -CapturePath 'C:\tmp\openblade-lighting-spectrum-random-oracle-v1\capture.pcap' `
    -AnnotationPath 'C:\tmp\openblade-lighting-spectrum-random-oracle-v1\lighting-spectrum-random-annotations.json' `
    -OutputPath 'C:\tmp\openblade-lighting-spectrum-random-oracle-v1\sanitized-evidence.json'
```

The analyzer verifies the complete reviewed action sequence, exact target,
finalized PCAP hash and size, report checksum, and captured 86-key mask. It
emits only relative RGB/HSV, cadence, observed wrap, and black-bounded breathing
measurements. It does not fit a renderer curve or infer a palette, PRNG, or
seed, and it never copies the raw PCAP into the repository.

Decoded PID `02E0` evidence lives under `captures/decoded`. It intentionally
uses an evidence schema rather than the production fixture schema so incomplete
gates, admitted validation cycles, multi-command operations, expected ACKs, and
readback candidates remain explicit. Files under `captures/annotations` contain
only sanitized UI action timestamps used for correlation. Neither directory by
itself authorizes a hardware write; admission additionally requires an exact
embedded production-fixture hash in the code-controlled registry.

There is intentionally no command for sending arbitrary reports. Hardware validation is implemented through typed, gated operations only.

## Typed OpenBlade lighting validation

Lighting uses a separate installed-build validation because PID `02E0` does
not expose matrix-effect readback. Run only the Capture executable installed
beside the exact `OpenBlade.Protocol.dll` being validated:

```text
OpenBlade.Capture validate-lighting --confirm-target RZ09-0581:1532:02E0:3.01 --brightness 60
```

The command repeats the exact target phrase interactively before opening a HID
handle. It clearly states that it cannot recover an unknown externally owned
effect, establishes a known Static green baseline, applies every typed effect
and Reactive duration, performs the independent brightness query after every
setting, asks for visual confirmation, and finally restores that known
baseline. It has no raw-command or raw-matrix input.

Stop the OpenBlade service before starting the command. The validation CLI
refuses to open hardware while that service is running and uses strict conflict
checks, so active Razer controller processes or services also block the run.
Normal installed OpenBlade operation remains advisory and reports those Razer
controllers without stopping them.

Only an all-gates success atomically replaces
`%ProgramData%\OpenBlade\lighting-validation.json`. The result is bound to the
target model, VID/PID, BIOS, full informational version, 40-character commit,
and SHA-256 of the exact loaded `OpenBlade.Protocol.dll`. Failed and canceled
runs write timestamped `.failed-*.json` sidecars and cannot replace an existing
admission artifact. The service fails closed when the artifact is absent,
malformed, over 64 KiB, incomplete, or does not match its current protocol
binary. Restart the OpenBlade service after a successful validation so it can
reevaluate admission.

## Adapter wattage readback

On this exact device, the read-only `07/8C` query with payload `0000` reports
the adapter wattage level. Captured response `0711` corresponds to 100 W USB-C
PD operation and `1111` corresponds to the 280 W OEM full-power adapter.
Synapse independently corroborates the low-power state by logging current level
100, recommended level 280, and forcing its AC balanced thermal mode. Any other
two-byte response is treated as unknown, and malformed responses fail protocol
validation. This query never authorizes a write.

## Wireshark extraction notes

Wireshark 4.6 decodes this device's control transfers reliably with the HID report fields even when `usb.setup.bRequest` and `usb.setup.wValue` are blank in `-T fields` output. Select request frames with:

```text
usb.device_address == <verified-session-address> &&
usb.transfer_type == 0x02 &&
usbhid.setup.ReportType == 3 &&
usbhid.setup.ReportID == 0 &&
(usb.bmRequestType == 0x21 || usb.bmRequestType == 0xa1)
```

Correlate the request and completion using `usb.irp_id` (`usb.urb_id` on captures that expose it) or Wireshark's `request_in`/`response_in` links. For a SET_REPORT request, `usb.data_len` can appear as 98 because USBPcap counts the 8-byte setup packet plus the 90-byte report envelope. The setup record still declares `wLength: 90`. A GET_REPORT completion contains the 90-byte response envelope. Windows reports `FeatureReportByteLength` as 91 because its HID API buffer also contains the leading report-ID byte; do not copy the USB setup bytes into a fixture.

USB device addresses are session-local and can change after reconnect or reboot. Re-enumerate the root topology and verify the target VID/PID before replacing `<verified-session-address>` for every capture.

## MCU version readback

The read-only MCU version query is class `00`, command `87`, with a four-byte
zero request payload and a four-byte response. In
`openblade-performance-fan-refresh-20260714.pcap`, frames 73/75/76 contain a
matching transaction whose response payload is `01 05 00 00`, decoded as
`1.5.0.0`. The sanitized envelope and source hash are recorded in
`captures/decoded/rz09-0581-pid-02e0-bios-3.01-mcu-version-fixture.json`.

Razer's published Synapse bundles independently identify `00/87` as "Get
Extended Firmware Version", decode the response as
`major.minor.internal.reserved`, store it as `firmwareInfo.currentFWVersion`,
and present that value as MCU. This is distinct from the shorter standard
firmware query `00/81`.

## Performance and fan readback discriminator

The existing no-mutation Performance-page capture exposed query-shaped
responses for `0D/80`, `0D/89`, `0D/8A`, `0D/8B`, `0D/8E`, and `0D/8F`.
The fixed read-only discriminator subsequently queried all six commands while
Synapse visibly held `Balanced+Auto`, `Silent+Auto`, `Performance+Auto`, and
`Balanced+Fixed5400`. Every response was identical in all four states, so these
commands are now disqualified as performance or fan-mode readback.

Do not repeat ordinary page-navigation captures or assign semantics to these
responses. The cold Synapse visible-UI startup capture found one additional
query, `0F/98`, with response `00` in `Balanced+Auto`; the original six
candidates were absent. The fixed discriminator then returned the same `00`
response for `0F/98` in `Silent+Auto`, `Performance+Auto`, and
`Balanced+Fixed5400`, disqualifying all seven candidates as performance or
fan-mode readback. Production capabilities remain empty.

For lighting, perform the same no-mutation page-hydration comparison with two
pre-established brightness values. Existing setter responses are same-
transaction acknowledgements, and interface 3 is a host-rendered 374-byte
matrix stream without GET reports. If reopening the Lighting page exposes no
successful query whose response tracks the pre-existing brightness, brightness,
Off, effects, and color remain unadmitted.
