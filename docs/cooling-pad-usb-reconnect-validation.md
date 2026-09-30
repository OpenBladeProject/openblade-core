# Cooling-pad USB reconnect validation

Tracking: [issue #119](https://github.com/OpenBladeProject/openblade-core/issues/119).
This follows the successful attended fan sequence and normal-exit persistence
observation. It tests persistence of the restored quiet Auto baseline when USB
is disconnected with 12 V retained. It sends no pad setter and does not test
interruption during Fixed, process crash, sleep or power loss.

## Exact scope and preflight

Use exact RZ09-05289EN4 and pad 1532:0F43 revision `0200`, MI_00 usage `000C:0001`,
91-byte Windows Feature buffer with prefix zero. BIOS is provenance only.
Synapse, both named Razer services and USBPcap remain stopped. All previous
pad-control actors must have exited. Keep the laptop's admitted thermal service
running and pad-dependent HyperBoost inactive.

Save a fresh metadata-only inventory, build/source hash and one read-only raw
control-state query. Proceed only after raw `0000` and a present operator's normal
quiet Auto/full-brightness static-green confirmation, with USB and 12 V connected.
Each private output uses a new path; preserve prior records and do not rerun a
command merely because a reply is delayed.

## One attended action

1. Ask the operator to disconnect only the pad's USB for about five seconds,
   keep 12 V connected, and reconnect to the same USB port.
2. Record the operator's fan/lighting/red-or-sleep observations during the gap
   and after reconnect. Do not send any report during the disconnected interval.
3. Collect fresh metadata after the operator reports reconnect complete. Require
   the exact pad identity, revision and geometry again; do not reuse a stale handle.
4. Open a new strictly guarded session for one read-only raw control-state query.
   A raw `0000` result confirms only persistence of that candidate value.
5. Obtain a separate post-query physical observation. Quiet Auto and green are
   operator observations, not firmware fan-state or RPM readback.

Unknown enumeration, controller conflict, failed query, unexpected raw value or
abnormal physical behavior ends this check without setters, retries or automatic
recovery. Keep the device powered and report the actual result. The current user
instruction forbids agent-initiated Synapse use; do not reinterpret historical
recovery-acknowledgement metadata as permission to restart it. A full USB-plus-12 V
power cycle remains an attended contingency only when needed, with its actual
outcome recorded separately.

## Evidence boundary

Publish sanitized inventory facts, typed query hashes/lengths, UTC action times,
operator observations and recovery outcome. Preserve raw files privately. No
independent PCAP or native report trace is produced by this check. Never infer
that one reconnect proves new-device ownership, interrupted-write safety,
autonomous recovery, repeated reliability or production fan admission.

If this baseline check succeeds, design Fixed interruption and crash recovery
separately, with an explicit pre-interruption state and a reviewed restoration
path. No such interruption is armed by this document or by the read-only query.