# Snap Tap investigation

The product-736 vendor code exposes a Snap Tap mapping path for RZ09-0581.
Standalone enforcement without Razer's keyboard filter driver remains
unproven. No Snap Tap mappings, driver settings, or HID reports were written
during this investigation.

## Mapping and suppression paths

The inspected `mapping_engine.dll` is version `1.3.16.5`, from AppEngine
`4.0.827`, with SHA-256
`6EABDFDEDF797E042738B630D827C06F7A45DBE560EBAEC96C66698F88F3320A`.
The cached JavaScript definitions came from `4.0.821`; this version difference
limits conclusions about an unobserved end-to-end execution.

The no-filter device path retains a HID handle and reaches shared Snap Tap
mapping computation. That establishes a reachable calculation path, rather
than proof that ordinary keyboard input can be suppressed without the filter.
The suppression hook belongs to a separate OS-hooking device.

| Traced operation | DLL offset | Result |
| --- | --- | --- |
| `ReadKeysFromLocalStorage` | `0x562C8` | Registers the separate `synapseGlobalShortcuts` key at `0x56361`–`0x56378`. |
| `ReadDeviceKeys` / callback | `0x77F10` / `0x780B0` | Reads the product/identity-specific `synapse_PID_identity` key and applies device data. |
| `NotifyGlobalShortcutDataChanged` / callback | `0x773C0` / `0x77550` | Updates the OS-hooking object's own global-shortcut data. |
| `HookGetGlobalShortcutMappedKey` | `0x66870` | Reads that object's lookup state at `+0x90`, `+0x3C8`, and `+0x3D0`. |
| `HookOnInputHook` | `0x121B84` | Delegates through the hook object initialized from the OS-hooking device at `+0x78`. |

The traced submission path does not mirror product-736 Snap Tap mappings into
the OS-hooking device's global-shortcut state. Retained vendor logs separately
show global-shortcut and product-device data changes, consistent with those
storage routes. No observed event connects a Snap Tap mapping update to the
hook's lookup state.

This leaves a concrete gap: a filter-free implementation needs a demonstrated
way to suppress the original conflicting key events while producing the
resolved pair. The vendor's ordinary filter route supplies the established
path. The examined no-filter path and separate global-shortcut hook do not yet
establish an alternative. Untraced indirect behavior has not been exhaustively
excluded.

OpenBlade has not added a Snap Tap backend or a kernel driver. Further work
should resolve that suppression boundary before proposing a writable mapping
integration.
