# Optional THX audio controls

OpenBlade can connect to the supported THX components already installed on a
Razer Blade 16 RZ09-0581 (`1532:02E0`). The integration is experimental and off
by default. Standalone operation with Synapse closed still needs physical
validation; the implementation has only been tested with a simulated backend.

OpenBlade does not install THX software or drivers. Other Blade models and
different THX component builds are not yet supported.

## Use the controls

1. Close Synapse, then open **Laptop > THX audio** in OpenBlade.
2. Enable **Use installed THX audio**. This checks the model and installed files.
3. Select **Speakers** or **Headphones**, then choose **Read settings**.
4. Once the current settings appear, change Spatial audio, Equalizer, or the
   equalizer preset. Presets come from the selected endpoint's THX component.

Reading settings connects to the THX component. Enabling the integration,
starting OpenBlade, changing the output selection, and closing OpenBlade do
not apply audio settings. Turning the integration off also leaves the current
audio settings in place.

If a change cannot be confirmed, use **Read settings** before trying another
change. OpenBlade does not repeat an uncertain command. If the supported
components are unavailable, the audio controls remain disabled. Uninstalling
Synapse may remove required components; their retention has not been tested.

## Component and process boundaries

The current admission is specific to the installed `rznb12` THX stack and
`ThxV4Native_v1.1.1.0.dll`. RZ09-0528 (`02C6`) does not inherit that product ID
or driver admission. A new component build needs its own source review and
validation.

The tray stores an additive per-user opt-in preference. It uses a separate
current-user/session Audio IPC endpoint, version 1. The existing Display and
GPU IPC versions are unchanged. This change requires a coordinated full
installation; it is not eligible for tray-only deployment.

The Session Agent serializes typed audio requests. A bounded helper process
accepts a single request over a private connection to its verified parent and
loads the pinned native client only for an explicit read or setting change.
The helper belongs to a kill-on-close Windows job before it receives work.
An unconfirmed helper exit blocks further operations until its exit is confirmed.
The client talks to the separately installed THX properties service, while
the Windows Audio APO performs audio processing. The helper does not open
the laptop control HID handle, launch Synapse, install drivers, or elevate.
OpenBlade's existing hardware ownership and competing-controller checks are
unchanged.

Discovery checks exact host identity, present USB identity, and the pinned
installed components without loading the native DLL. Each operation repeats
admission. Successful setting changes require matching readback. A timeout,
crash, or ambiguous mutation pauses further changes until the helper has exited
and an explicit read reconciles the state. Audio preferences are not added to power profiles and
are not automatically restored at startup or shutdown.

## Vendor evidence

The source inspection on 2026-10-16 used official cached Synapse product-736
bundles. Product 736 is decimal PID `02E0`; its configuration explicitly sets
`thxProductID` to `rznb12`. The shared middleware's fallback `rznb11` is not
used as evidence for this laptop.

| Input | SHA-256 |
| --- | --- |
| Cached `products/736/mw/main.b2e1b23c426e9bf93af6.js` entry, including cache metadata | `91B8536142593F30F4AD9A53B7EEC701613C7B2E35D689479734976B65DFD9D0` |
| Cached `products/736/mw/6737.45e4affd7675b37f3c36.js` entry, including cache metadata | `AF329F52E1F4F1F9A4F823D32A2B113D5D485D7F0EA0EB5EE1F0EACCC06D86CC` |
| Installed `ThxV4Native_v1.1.1.0.dll` | `8E8F6923624762935D70B72F58BEC00808DB84DF740340047919B53331130A0A` |

The vendor middleware declares `AddDevice`, `CheckStatus`, `GetProperty`,
`SetProperty`, preset operations, `RemoveDevice`, and `FreeMalloc`. Device
arguments contain the THX product ID and the `speakers` or `headphones`
endpoint. Native strings identify a local gRPC properties endpoint under
`ProgramData/thx`; the installed service INF registers an automatic,
independent `thx_rznb12_service`. The audio package registers Windows Audio
APOs. These establish the source-backed standalone architecture, not an
observed standalone audio result.

Synapse startup explicitly enables EQ, and shutdown explicitly disables EQ
before unregistering its client. That shutdown path does not explicitly
disable spatial audio. OpenBlade does not copy those EQ side effects.

The cached updater metadata referenced THX `4.2.37.1846`; the inspected
installed driver package is `4.2.41.2499`. They are separate evidence
snapshots. No vendor code was loaded, no THX API was called, and no audio
settings or installed processes were changed during implementation.

## Remaining validation

The draft implementation needs separately authorized standalone native and
audio validation on the exact admitted stack. Check endpoint selection,
native registration and cleanup effects, supported presets, confirmed
readback, interruption handling, and preservation of the original settings.
Keep the change draft until those results are recorded. Any UI preview must
use a disconnected, simulated backend and must not connect to the installed
Session Agent's THX endpoint.
