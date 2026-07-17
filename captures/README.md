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

Decoded PID `02E0` evidence lives under `captures/decoded`. It intentionally
uses an evidence schema rather than the production fixture schema so incomplete
gates, admitted validation cycles, multi-command operations, expected ACKs, and
readback candidates remain explicit. Files under `captures/annotations` contain
only sanitized UI action timestamps used for correlation. Neither directory by
itself authorizes a hardware write; admission additionally requires an exact
embedded production-fixture hash in the code-controlled registry.

There is intentionally no command for sending arbitrary reports. Hardware validation is implemented through typed, gated operations only.

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
