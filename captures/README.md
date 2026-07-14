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
8. Run `OpenSynapse.Capture validate fixtures.json`. It must fail until interactive application, readback, and restoration have each been confirmed. After review, add the exact fixture-file SHA-256 to the code-controlled production registry; fixture metadata alone cannot authorize commands.

There is intentionally no command for sending arbitrary reports. Hardware validation is implemented through typed, gated operations only.
