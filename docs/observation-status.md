# Typed device observation status

`ServiceStatusSnapshot.ObservationReason` describes why an observation retains
previously confirmed state. `Message` is diagnostic text; changing its wording
must not change service, tray, or Session Agent behavior.

| Reason | Meaning |
| --- | --- |
| `None` | No transient-observation exception is supplied. |
| `CheckingConnection` | A bounded device-probe retry retains the prior presentation. |
| `RefreshingControls` | A bounded device-access retry retains confirmed controls. |
| `RetainedPowerSource` | Power classification is uncertain and retains the last known source. |

The reason never authorizes a hardware write. Existing device identity, session,
power, controller, and confirmed-state admission still applies. Tray retained-power
presentation additionally requires verified status, blocked writes, and a retained
PluggedIn or USB-C source. Session Agent temporary reactive-state retention uses
only the two device-access reasons and retains its existing input safety gates.

Metadata and diagnostic-message updates preserve the observation reason. Fresh
observations replace it; exhausted retries and definitive unavailability clear it.
A successful power-only check must not claim that device access has recovered.

## Compatibility

The closed service IPC protocol advances from 54 to 55 because old readers reject
unknown properties. Protocol 54 peers are rejected explicitly; all current service
clients must be updated together. The existing protocol-51 read-only projection
omits both observation reason and thermal convergence state and continues to block
hardware mutations. No new legacy control path is introduced.

A missing reason defaults to `None`; readers do not infer a reason from message
text. Undefined enum values are rejected by the codec. The reviewed wire-shape
and public API baselines cover the new enum and snapshot property. This change
requires a coordinated full product update, not a tray-only binary patch.
