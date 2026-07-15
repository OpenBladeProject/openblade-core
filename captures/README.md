# PID 02E0 capture workflow

Production hardware writes remain disabled until every command has passed all five gates: captured, decoded, applied interactively, read back, and restored.

## Prerequisites

- Target: Razer Blade 16 `RZ09-0581`, `1532:02E0`, BIOS `3.01`.
- Synapse `4.0.695` remains installed only for this capture phase.
- Wireshark and USBPcap are installed. Reboot Windows before capture so the USB filter becomes active.

## Procedure

1. Close unrelated Chroma applications and set keyboard lighting Off to reduce traffic.
2. Start USBPcap on the root hub containing `VID_1532&PID_02E0`, restricted to that device address and capturing full packets with descriptors. Never force-kill the capture process.
3. Change exactly one Synapse setting, wait for its response, and annotate the timestamp and action.
4. Restore the previous value before moving to the next action.
5. Stop USBPcap with Ctrl+C so the PCAP is flushed and the root handle is released. The helper in `captures/tools/Stop-UsbPcapCapture.ps1` sends that signal only after confirming the target process is `USBPcapCMD`.
6. Export only the matching feature-report request and response bytes. Do not commit raw PCAP files, serial numbers, or unrelated USB traffic.
7. Create a sanitized fixture entry with the exact device identity and all validation flags initially false except `captured` and `decoded`.
8. Run `OpenBlade.Capture validate fixtures.json`. It must fail until interactive application, readback, and restoration have each been confirmed. After review, add the exact fixture-file SHA-256 to the code-controlled production registry; fixture metadata alone cannot authorize commands.

Decoded but unadmitted PID `02E0` evidence lives under `captures/decoded`. It intentionally uses an evidence schema rather than the production fixture schema so incomplete gates, multi-command operations, expected ACKs, and readback candidates remain explicit. Files under `captures/annotations` contain only sanitized UI action timestamps used for correlation. Neither directory authorizes a hardware write.

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
