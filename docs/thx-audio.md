# Optional THX audio controls

OpenBlade can connect to the supported THX components already installed on a
Razer Blade 16 RZ09-0581 (`1532:02E0`). The integration is experimental and off
by default. Reading settings and reversible speaker and headphone changes with
Synapse closed have been validated on the admitted stack. Audible processing
still needs validation.

OpenBlade does not install THX software or drivers. Other Blade models and
different THX component builds are not yet supported.

## Use the controls

1. Close Synapse, then select **THX Audio** in the left menu in OpenBlade.
2. Enable **Use installed THX audio**. This checks the model and installed files.
3. Select **Speakers** or **Headphones**, then choose **Read settings**.
4. Once the current settings appear, change Spatial audio or Equalizer, or
   choose Game, Movie, Music, Voice, or Custom. An observed Default preset is
   retained so its exact settings can be restored.
5. Adjust the selected preset’s EQ bands, then choose **Apply EQ**.
   **Reset to factory** stages that preset’s original curve; **Flatten** stages
   zero gain. **Discard changes** returns to the last confirmed curve.
6. Open **Enhancements** for Voice clarity and speaker Sound normalization.
   Turn an enhancement on, adjust its level, then choose its Apply button.

Speakers have seven EQ bands; headphones have ten. Both support -12 to +12 dB.
Editing Music keeps Music selected; Custom is also available as its own preset.
An observed Default or application preset must be changed to one of the five
EQ choices before editing. Preset selection applies the complete saved curve.
Application-specific THX
profiles are separate from the five EQ choices.

## Saved audio and applications

Open **Saved audio and applications** to capture both outputs and save their
settings to the current power profile. Select an EQ preset for each output
before the first capture; an unknown application profile or native Default
preset is not silently converted into another EQ choice.

Add rules using executable names such as `game.exe`. Every application can use
a base EQ preset; supported games also offer THX Immersive or Competitive when
the installed component reports the corresponding profile. Save the profile
to keep the rules.

**Apply saved audio and application rules automatically** is off by default.
When enabled, the foreground application selects a matching rule; otherwise
OpenBlade uses that power profile's saved general audio settings. Automation
also requires **Use installed THX audio**. Closing the tray leaves enabled
automation with the Session Agent. Unresolved foreground identity preserves
the current state. Hosted applications whose executable cannot be identified
are not yet supported for automatic switching.

Automatic changes target only the recognized Windows default Realtek output.
An external, missing, or ambiguous default output leaves THX unchanged.

Reading settings connects to the THX component. Enabling the integration,
starting OpenBlade, changing the output selection, and closing OpenBlade do
not apply audio settings unless saved audio automation was explicitly enabled.
Turning the integration off stops automation and leaves the current audio
settings in place.

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
current-user/session Audio IPC endpoint, version 1. Saved audio uses profile
store schema 21, export schema 13, and service IPC version 67. Existing Display
and GPU IPC versions are unchanged. This change requires a coordinated full
installation; it is not eligible for tray-only deployment.

The Session Agent serializes typed audio requests. A bounded helper process
accepts a single request over a private connection to its verified parent and
loads the pinned native client only for an explicit read or setting change.
The helper belongs to a kill-on-close Windows job before it receives work.
An unconfirmed helper exit blocks further operations until its exit is confirmed.
The native DLL stays mapped until the one-shot helper exits because its async
callbacks can outlive client unregistration. Windows provides this lifetime
through [module pinning](https://learn.microsoft.com/en-us/windows/win32/api/libloaderapi/nf-libloaderapi-getmodulehandleexw).
The client talks to the separately installed THX properties service, while
the Windows Audio APO performs audio processing. The helper does not open
the laptop control HID handle, launch Synapse, install drivers, or elevate.
OpenBlade's existing hardware ownership and competing-controller checks are
unchanged.

Discovery checks exact host identity, present USB identity, and the pinned
installed components without loading the native DLL. Each operation repeats
admission. Successful setting changes require matching readback. A timeout,
crash, or ambiguous mutation pauses further changes until the helper has exited
and an explicit read reconciles the state. Optional foreground automation
shares the same serialized dispatcher with manual controls. Manual control
interrupts pending automation; uncertain writes are never replayed. Startup
and shutdown do not reset audio settings.

## Vendor evidence

The source inspection on 2026-10-16 used official cached Synapse product-736
bundles. Product 736 is decimal PID `02E0`; its configuration explicitly sets
`thxProductID` to `rznb12`. The shared middleware's fallback `rznb11` is not
used as evidence for this laptop.

| Input | SHA-256 |
| --- | --- |
| Cached `products/736/mw/main.b2e1b23c426e9bf93af6.js` entry, including cache metadata | `91B8536142593F30F4AD9A53B7EEC701613C7B2E35D689479734976B65DFD9D0` |
| Cached `products/736/mw/6737.45e4affd7675b37f3c36.js` entry, including cache metadata | `AF329F52E1F4F1F9A4F823D32A2B113D5D485D7F0EA0EB5EE1F0EACCC06D86CC` |
| Extracted product-736 UI `8923.c2f78964.chunk.js` | `5BE64A2DF27D5E8ECEC1B3C6007A4DFD1CFD975C93D0A366F1D801B1DBF78B14` |
| Extracted product-736 UI `main.22abe6bb.js` | `75B2D6F6180F5F0B09239E8CCA088D86143E539E1AAF24FFBB0E47C2B651A08B` |
| Installed `ThxV4Native_v1.1.1.0.dll` | `8E8F6923624762935D70B72F58BEC00808DB84DF740340047919B53331130A0A` |

The navigation icon uses the THX mark from the product-736
[`appmix_thx.6833fdbb.svg`](https://apps.razer.com/synapse/products/736/ui/static/media/appmix_thx.6833fdbb.svg)
asset, SHA-256 `D55BE865AB57F2613E6785D3D2CC6E683977416CFCCD55196DE6A6676F2A20F0`.
The original asset is 24×12 with a green backplate; the navigation uses its
vector mark with the current theme foreground.

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

The exact product-736 profile factory (`main` module 26061) supplies five
initial curves per output. Its `getDefaultProfile` function is used by the
middleware's new/reset paths. Older curves in the UI bundle are fallbacks when
middleware data is absent. Saved profile curves take precedence thereafter.
The native mapping is Game → `game`, Movie → `movie`, Music → `music`,
Voice → `podcast`, and Custom → `custom`. The middleware selects that native
profile and then writes all ten `eq_band_N_gain_db` Float properties in order;
the speaker path pads its first three hidden bands with zero. OpenBlade follows
that complete sequence and confirms the resulting selected ID and gains.
The product UI edits only the selected preset. Its Reset action restores that
preset’s factory curve, while its zeroing action flattens that curve; neither
resets all presets. OpenBlade stages these edits until **Apply EQ**.
Any failure after a possible submission stops the sequence without retry or
guessed restoration.

Sound normalization uses `volume_leveling_enabled` and
`volume_leveling_level`; Voice clarity uses `voice_clarity_enabled` and
`voice_clarity_level`. Enable flags use Boolean JSON with a null native type;
levels use Float values normalized from 0–100 to 0–1. The headphone product UI
does not admit normalization. EQ curves and enhancements are separate settings.

Per-application routing matches the foreground executable case-insensitively.
The product's THX v4 runtime selects module 74830's 231-entry speaker map via
`getGamePresetMappings(true, thxv4)`. OpenBlade pins that exact mapping and
intersects its Immersive/Competitive IDs with each endpoint's current native
catalog. Native IDs remain internal. `THXSpatialAudioClient.exe` is a competing
controller; routing does not require launching it.

The cached updater metadata referenced THX `4.2.37.1846`; the inspected
installed driver package is `4.2.41.2499`. They are separate evidence
snapshots. Initial implementation used static inspection and simulated calls;
the later authorized read-only runtime validation is recorded below.

## Read-only runtime validation

On 2026-10-16, both exact RZ09-0581 host identity sources, present USB
`1532:02E0`, the pinned component hashes, and the running THX service passed
production admission. The native DLL had a valid Razer signature. The user
closed Synapse; no AppEngine process was present during the queries.

An isolated developer runner used the production dispatcher, authenticated
helper pipe, Windows job, and native adapter. Its adapter rejected
`SetProperty` and `SelectPreset` before any native invocation. It queried
Speakers, Headphones, Speakers, then Headphones, with fresh admission each time.

| Endpoint | Spatial audio | Equalizer | Selected preset | Presets |
| --- | --- | --- | --- | --- |
| Speakers | Off | Off | `music` | 218 |
| Headphones | Off | Off | `default` | 218 |

Repeated states matched. All four `AddDevice`, `CheckStatus`, and
`RemoveDevice` calls succeeded; all helpers exited normally. No setting or
preset mutation was invoked, no helper remained, and the THX service remained
running. This confirms standalone state access, not audible processing or
setting-change behavior. The first query establishes the available baseline;
it cannot prove state from before client registration.

The first attempts exposed three implementation gaps, corrected in
`6b955bd04e1f9e7e91d77b917a912053d72a6f45`: the 218-entry catalog exceeded
the original 128-entry limit; the selected preset was a bare UTF-8 ID rather
than a JSON string; and unloading the DLL after successful unregistration
caused a helper access violation in its unloaded image. The list limit is now
256 with the independent 64 KiB frame limit retained. Selected IDs remain
bounded and must belong to the endpoint's returned catalog. Pinning the native
image until helper exit resolved the observed unload failure. Successful reads
were repeated after these corrections, with no new helper crash events.

## Reversible speaker validation

On 2026-10-16, separately authorized validation exercised the three exposed
speaker controls through the same production dispatcher and native helper:

| Control | Applied and confirmed | Restored and confirmed |
| --- | --- | --- |
| Spatial audio | On | Off |
| Equalizer | On | Off |
| Preset | `default` | `music` |

Before any write, the runner durably saved both endpoints' complete confirmed
states. Speakers started with spatial audio Off, EQ Off, and preset `music`;
headphones started with both switches Off and preset `default`. Each round trip
checked the immutable baseline hash and fresh speaker state, admitted one exact
typed change, confirmed it by readback, read again before restoration, and
restored the saved value. It then independently read both endpoints and required
their complete states, including the 218-entry catalogs, to match the baseline.

All three round trips passed. The six setting writes comprised four
`SetProperty` calls and two `SelectPreset` calls. Every helper cleanup and exit
was confirmed, with no retry, ambiguous result, surviving validation process,
or new helper crash. Synapse stayed closed and the THX service stayed running.
The final baseline was exact: speakers Off/Off/`music`, headphones
Off/Off/`default`. No headphone setting was written.

The developer runner restricted each helper to one exact mutation ticket for
`rznb12` speakers and stopped further writes on any failed response, unexpected
state, or missing cleanup audit. The baseline SHA-256 was
`0964A8A663DC6D543BC423BA0D4526DCD57B0CCCC94BBBE25701DF0B3153243B`.
The tested production code was `b4f30e347efec689845c71851b5d57591d4244ca`;
this validation changed no product code, installed component, or HID state.

## Reversible headphone validation

A separately authorized run on 2026-10-16 repeated the three round trips on
headphones with fresh baseline queries and the same production code and
component pins. The runner changed only its typed target, prior-state
comparison, and native mutation guard from speakers to headphones. Both
endpoints' complete states remained part of every final baseline comparison.

| Headphone control | Applied and confirmed | Restored and confirmed |
| --- | --- | --- |
| Spatial audio | On | Off |
| Equalizer | On | Off |
| Preset | `music` | `default` |

All six writes and all three exact restorations passed, with no failed cleanup,
surviving helper, or new helper crash. Speakers stayed Off/Off/`music` and
headphones finished Off/Off/`default`, each with 218 advertised presets. The
fresh baseline hash matched the earlier baseline:
`0964A8A663DC6D543BC423BA0D4526DCD57B0CCCC94BBBE25701DF0B3153243B`.
No speaker setting was written in this run. The tested production code was
unchanged from `b4f30e347efec689845c71851b5d57591d4244ca`.

## Expanded EQ and enhancement validation

On 2026-10-16, the expanded helper read ten native EQ gains, enhancement
settings, exact-product factory curves, and 231 available executable mappings
on each endpoint. Speakers had visible gains `0,0,1,2,3,4,0`; headphones were
flat. Both EQ/spatial switches were off, and both Voice clarity levels were
exactly zero. The read-only adapter rejected every setting mutation.
The active Windows default output was identified as Speakers.

Two further authorized headphone round trips used the production dispatcher
and isolated helper, with Synapse closed:

| Test | Applied state | Restoration | Native calls |
| --- | --- | --- | --- |
| Custom EQ | Custom, 31 Hz at +1 dB; other bands zero | Default and all ten original zeros | Two preset selections, twenty Float writes |
| Voice clarity | Enabled, level 25% | Level zero while enabled, then disabled | Four property writes |

Neither test enabled EQ or spatial audio or changed speaker settings. Restoring
the Voice clarity level before disabling follows the vendor's enabled-only
level setter. Each step passed fresh identity/controller/session admission and
matching readback. Every helper's cleanup and exact ordered ticket were checked;
both endpoints were independently reread after each complete restoration.

All sixteen expanded-query/write helpers exited normally. No matching crash
occurred during these runs, and no helper remained. The immutable expanded
baseline SHA-256 remained
`7AE95C6B89B5478B23E427EA08E79DFD53BCEB96158FFD18DD8A1DE2102CBFBA`.
The tested Session Agent assembly SHA-256 was
`C51289354AE00E58A3760839E4F659E3F27D84739293179E53D2B62F045857CB`.
Application catalogs were compared in canonical ordinal order; all native
queried setting values and selected IDs matched the original baseline.

## Remaining validation

The draft implementation still needs separately authorized audible-processing
and interruption validation on the exact admitted stack. No playback or
audio-quality test was performed by these runtime runs. The first foreground
observation was Unknown, so live known-foreground switching remains unvalidated.
Immersive/Competitive native selection, active-headphone output detection,
and speaker-normalization writes also remain untested.
Keep the change draft until those results are recorded. Any UI preview must
use a disconnected, simulated backend and must not connect to the installed
Session Agent's THX endpoint.
