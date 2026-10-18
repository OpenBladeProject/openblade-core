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

## Implementation boundary

This recovery replaces the earlier claim that another capture was necessary
to identify the report's offsets, Fn IDs, and release semantics. It does not
enable runtime input support in this PR. A later implementation can use this
exact-model decoder with current-host and HID identity/geometry admission,
per-device state, repeat suppression, and state reset on disconnect or session
changes. It must correlate Fn with the ordinary P-key edge separately for
Fn+P and pass through unknown or ambiguous input.

No programmable M-key slots, function-key-primary writes, Gaming Mode,
keyboard Driver mode, or other firmware capability follows from decoding this
read-only input report. No hardware writes, input hooks, injection, driver
changes, or capture were performed during this investigation.
