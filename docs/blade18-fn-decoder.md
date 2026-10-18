# Blade 18 Fn decoder recovery

Static review on 2026-10-18 recovered the wired RZ09-0484 (`1532:02A0`)
Razer-key decoder from Synapse's exact product configuration, shared mapping
SDK, and native mapping engine. A new capture is not required to establish
this decoder. Live input availability and shortcut behavior remain untested.

## Pinned sources

| Source | SHA-256 |
| --- | --- |
| [Product 672 main](https://apps.razer.com/synapse/products/672/mw/main.57245df0507455a5119e.js) | `D523F5080784B07CB1888C39E636004626E073E2967B85999498030102F85FAE` |
| [Shared product runtime](https://apps.razer.com/synapse/products/672/mw/6737.e50bb836b878190315d7.js) | `651F63DCC0EFC92B0FD7496EE70751707B5F650F20AF5C01C3255B61817C1997` |
| [Mapping SDK](https://apps.razer.com/synapse/products/672/mw/7795.8e8d6b06ca4c3fe23fe1.js) | `13F46F9584090A9D50433C0F6B55E2004C6F398BD5D0D45551367B33F6E32BBD` |
| `SysUtilsNative.dll` (installed Synapse, inspected offline) | `01223BFABF0355F42705836974E343A1F16F837D692999020D218B2615B8E318` |
| `mapping_engine.dll` 1.3.16.5 | `6EABDFDEDF797E042738B630D827C06F7A45DBE560EBAEC96C66698F88F3320A` |

The 142,996-byte SDK is an initial dependency named by the pinned main bundle.
All sources were read as data. The DLL was inspected through its PE tables and
x64 disassembly; it was never loaded or executed. Native addresses below are
RVAs relative to image base `0x180000000`. JavaScript offsets are zero-based
UTF-8 byte offsets.

## Exact-model source chain

Product 672 declares category `SYSTEM` and maps report `4` to
`razerKeyReportID`, report `5` to `hardwareEventReportID`. It supplies no
`bluetoothLE` framing flag or `keyIdToKeyInput` override. The owner's existing
inventory corroborates two 16-byte Generic Desktop input collections, but its
exported geometry does not retain their report IDs.

The shared runtime calls `setDefaultMappings(..., DeviceInfo.category)` near
bytes 1230269 and 1250497. The SDK's default builder starts near byte 117469
and appends the Fn mappings near byte 118318. Its Fn factory near byte 127616
uses `KEYBOARD: [1]` and **`SYSTEM: [10, 11]`**. For each selected ID, it emits
`razerKey` make/release inputs that produce `hypershift` make/release outputs,
in both normal and already-shifted states. This establishes raw IDs `0x0A`
and `0x0B` for this product's default Fn layer.

`FnKey: 1` in a separate enum is not the SYSTEM mapping. Neither
`HyperShiftButton: 89` nor calibration `analogKeyID: 59` supplies the raw Fn
value. The sources do not identify which of `0x0A` and `0x0B` corresponds to
LEFT_FN or RIGHT_FN, or which one the physical Blade 18 key emits. Recognizing
either matches the exact SYSTEM default mapping without inventing that label.

## Native decoder

| Native location | Verified behavior |
| --- | --- |
| `0xE3F1E`, call `0xE40D1` | Loads the configured report-ID map through `0xE7080`. |
| `0xE3BC8`, table `0x2EC310` | Maps `razerKeyReportID` to enum 6, hardware events to enum 8. |
| `0xE3878`, store `0xE393D` | Initializes configuration bytes including `+0x8D` to zero. The optional Boolean loader leaves the absent Bluetooth flag unchanged. |
| `0xA8DE0`, lookup `0xA9051` | USB dispatcher selects the configured handler from the report-ID byte. |
| Table `0x2E81F8`, entry 6 | Routes to `0xA918F`; the unprefixed USB path calls parser `0x7FE46` at `0xA9243`. |
| `0x7FF0B`–`0x7FF5E` | Skips the report-ID byte, scans the remaining byte-valued key IDs, and ignores zero padding. |
| `0x80031`–`0x8003B` | Emits a make for an ID newly present, with state 0. |
| `0x80329`–`0x80336` | Emits a release for a previous ID now absent, with state 1. |
| `0x104008` | Stores the unchanged raw key ID at event `+0x38` and state at `+0x3C`. |
| `0xEBD45`, call `0xEBE5C` | Parses mapping definitions and creates the same Razer-key event type through `0x104008`. |
| `0x103AE0`, comparisons `0x103B16`–`0x103B24` | Compares the key and flag fields directly, connecting SDK key 10/11 definitions to raw report bytes without conversion. |
| `0x6A330`, trampoline `0x6A470` | Uses that equality during mapping lookup, closing the runtime event-to-definition path. |
| `0xEB8B9`–`0xEBB1D` | Parses optional per-device key overrides; this is not an embedded Fn-number table. |

The recovered wired report is:

```text
byte 0       04                 report ID
bytes 1–15   key IDs or 00      concurrent pressed-key snapshot
```

There is no make/release flag byte in this report. Duplicate IDs collapse to
one set member. Presses are `current − previous`; releases are
`previous − current`. Fn is held when the current set contains `0x0A` or
`0x0B`, regardless of its slot. A repeated or reordered snapshot creates no
new edge. Report 5 is a separate hardware-event format.

For example, synthetic `04 0A 00 ...` holds Fn; `04 00 ...` releases it.
These examples and the offline reference tests are constructed from the
recovered algorithm, not owner-recorded key events.

## Default shortcuts

The product-672 mappings and shared SDK define these defaults:

| Fn combination | Action |
| --- | --- |
| P | Next performance preset |
| R | Next available fixed display refresh rate |
| T | Toggle the touchpad |
| F1, F2, F3 | Mute, volume down, volume up |
| F4 | Windows projection menu |
| F5, F6, F7 | Previous track, play/pause, next track |
| F8, F9 | Display brightness down, up |
| F10, F11 | Keyboard brightness down, up |
| F12 | Print Screen |
| Up, Left, Down, Right, Delete | Page Up, Home, Page Down, End, Insert |

SDK adapters disable T/P/R on down and execute their action on release.
OpenBlade retains that press decision through Fn release, suppresses repeats
for these three actions, and cancels them after loss of admission. Volume,
brightness and navigation repeat while held. Other defaults execute once.

Fn+T uses Windows' documented [Ctrl+Win+F24 touchpad toggle](https://learn.microsoft.com/en-us/windows-hardware/design/component-guidelines/touchpad-enable-or-disable-toggle-button).
Synapse's pinned SysUtilsNative toggle export at RVA 0x77DD0 sends the same
chord through USER32 keybd_event. Its status getter at 0x770F0 reads the
PrecisionTouchPad Status Enabled value. OpenBlade sends the documented chord
through its current-user input sender; it never writes that status registry
value, loads the vendor DLL, or changes touchpad drivers. This command has no
result readback in OpenBlade; actual Windows touchpad behavior remains untested.

## Runtime admission and verification

PR #786 now implements these defaults experimentally. The Session Agent accepts
only the verified RZ09-0484 host and PID 02A0 input identity, with 16-byte report-4
snapshots. It pins one report-4 collection during that input lifecycle; a
second collection makes Fn ambiguous until lifecycle reset. It does not assume
the older models' MI_01 collection or keyboard Driver mode.

The existing user-mode keyboard hook suppresses the original mapped key and
queues typed actions only in a confirmed local session with no other keyboard
present. A fresh, bounded Raw Input device list must identify every keyboard as
the Blade 18 before each suppressed edge. An unknown device, failed enumeration,
or remote session passes keys through and cancels pending actions. Remote
sessions require a separate check because Windows omits RDP devices from the
[Raw Input device list](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getrawinputdevicelist).
This restriction belongs to the Blade 18 backend, which has no verified
per-device suppression or keyboard Driver-mode path. Explicit Gaming Mode
blocking retains its existing all-keyboard policy.

Injected output is excluded from interception. A press
that began without Fn passes through until its release. A consumed press stays
consumed through its release even if Fn is released first; lifecycle or status
failure cancels its action. A topology change also clears consumption latches,
so keys from a newly connected keyboard pass through. Status reconnect,
power/session changes and device
arrival/removal clear held Fn evidence. Ordinary Blade 18 status updates with
no Driver-mode readback do not discard its independent Fn snapshot.

Fn+P uses the existing service performance shortcut path and its fresh hardware
admission. Fn+R uses the shared current-user display controller, advertised fixed
rates and existing power/readback checks. F-row brightness uses existing display
and keyboard lighting executors. Navigation uses typed Windows shortcuts.

Offline tests cover the decoder, exact identities, press/repeat/release latches,
cancellation, status admission and action routing. No hardware writes, live
input injection, installation, driver changes or new capture were performed.
Physical validation must still establish that the report is available without
Synapse, that Fn reports precede the ordinary keys, and that firmware or another
controller does not produce duplicate actions. The hook's ordinary keyboard
events have no device identity, so actions also wait for the matching Raw Input
keyboard identity and recheck topology before confirmation. Foreign-keyboard
packets cancel the tentative action. Missing hook edges reconcile releases
without replaying a missed down.
An unmatched-event overflow cancels pending actions. Test one attended press of
each materially different action path and check restoration where applicable.

No programmable M-key slots, remapping, function-key-primary writes, Gaming Mode,
keyboard Driver mode or additional firmware capability follows from this decoder.
