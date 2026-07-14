# PID 02E0 capture workflow

Production hardware writes remain disabled until every command has passed all five gates: captured, decoded, applied interactively, read back, and restored.

## Prerequisites

- Target: Razer Blade 16 `RZ09-0581`, `1532:02E0`, BIOS `3.01`.
- Synapse `4.0.695` remains installed only for this capture phase.
- Wireshark and USBPcap are installed. Reboot Windows before capture so the USB filter becomes active.

## Procedure

1. Close unrelated Chroma applications and set keyboard lighting Off to reduce traffic.
2. Start USBPcap on the root hub containing `VID_1532&PID_02E0`, capturing full packets with descriptors.
3. Change exactly one Synapse setting, wait for its response, and annotate the timestamp and action.
4. Restore the previous value before moving to the next action.
5. Export only the matching feature-report request and response bytes. Do not commit raw PCAP files, serial numbers, or unrelated USB traffic.
6. Create a sanitized fixture entry with the exact device identity and all validation flags initially false except `captured` and `decoded`.
7. Run `OpenSynapse.Capture validate fixtures.json`. It must fail until interactive application, readback, and restoration have each been confirmed.

There is intentionally no command for sending arbitrary reports. Hardware validation is implemented through typed, gated operations only.
