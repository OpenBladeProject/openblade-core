# Optional THX audio controls

OpenBlade can connect to the supported THX components already installed on a
Razer Blade 16 RZ09-0581 (`1532:02E0`), Blade 16 RZ09-0528 (`1532:02C6`),
or Blade 14 RZ09-0530 (`1532:02C5`). The integration is experimental and off
by default. Reading settings and reversible speaker and headphone changes with
Synapse closed have been validated on RZ09-0581. The user also confirmed an
audible spatial change on its built-in speakers after enabling Windows audio
enhancements. Other outputs and rznb11 audible processing remain unvalidated.

OpenBlade checks the exact laptop, present audio hardware IDs from the signed
installer INF, and a complete pinned THX stack before connecting. RZ09-0581
admits rznb12 4.2.41.2499 and the separately pinned 4.2.37.1621 build;
RZ09-0528 and RZ09-0530 use the reviewed rznb11
4.2.29.1159 driver stack with the official 4.2.37.1845 active configuration.
The immutable properties archive and surround configuration are pinned too;
mutable user settings remain editable. Other payloads are not yet admitted.
The two rznb11 models have vendor-backed controls and their own speaker EQ
curves, but have not been physically tested in OpenBlade.

OpenBlade can download the supported THX client from Razer. On RZ09-0581,
RZ09-0528 and RZ09-0530, it can also perform an experimental fresh installation
of the matching THX driver, service and configuration without Synapse installed.
The base audio drivers must already be installed: Realtek on RZ09-0528/RZ09-0530,
or Intel SST and Windows inbox SDCA on RZ09-0581. RZ09-0581 setup
uses the original signed 4.2.37.1621 installer recovered from the Windows cache
and separately hosted by the driver catalog with owner-confirmed redistribution
permission. Existing admitted stacks are retained. Pristine installation and
runtime control on this cached build still need physical validation. See the
[installation compatibility investigation](thx-installation-investigation.md)
for the package and codec restrictions.

## Coverage of supported laptops

| Model | Installed THX controls | Fresh THX setup |
| --- | --- | --- |
| RZ09-0581 | rznb12 spatial audio, EQ and application profiles | Experimental cached 4.2.37.1621 setup; Windows SDCA and matching Intel SST required |
| RZ09-0528, RZ09-0530 | rznb11 spatial audio, EQ and application profiles | Experimental, matching Realtek driver required |
| RZ09-0482, RZ09-0483, RZ09-0484 | Experimental spatial toggle through the installed THX 2.6.5.0 service | Endpoint binding and installation sequence remain unverified |
| RZ09-0328 | Factory audio uses Dolby Atmos; THX does not apply | Not applicable |

On the three 2023 models, select the output currently used by Windows. OpenBlade
checks the service's output identifier, Windows default output, speaker/headphone
form factor and exact Realtek codec ancestry before control. It does not switch
the output or listening mode. EQ, presets and automatic audio profiles remain
unavailable for this older THX version. Its native behavior has not been physically
validated in OpenBlade. The Store control app and Synapse are not needed for this
backend; the pinned factory audio components must already be installed.

Razer documents the older driver and Store control app in
[its THX setup guide](https://mysupport.razer.com/app/answers/detail/a_id/5798/).
The exact Realtek packages contain model-specific OEM codec tuning. The public
THX 2.6.5 SCU contains the model-specific PI490, SO690 and KI890 acoustic
profiles and selects them from the Realtek subsystem ID. A recovered signed
THX extension supplies the exact-model component-association rules. The matching
endpoint-effects binding and installation sequence remain unverified, so fresh
installation is blocked.
The [legacy catalog evidence](https://github.com/OpenBladeProject/blade-driver-catalog/blob/18917d99b064aafd3eb8b7cbee308e099c455948/components/thx-legacy/2.6.5.0/stack.json)
records verified packages and the remaining installation blockers. For recovery,
use the exact laptop's Windows driver updates or
[Razer recovery image](https://mysupport.razer.com/app/answers/detail/a_id/5997/).
See the [legacy interface review](thx-legacy-interface.md) for the pinned native
contract and COM activation boundary.

## Use the controls

1. Select **THX Audio** in the left menu in OpenBlade. If Synapse is installed, close it first.
2. Enable **Use installed THX audio**. OpenBlade checks the installed stack and
   reads the selected output automatically.
3. Select **Speakers** or **Headphones** to read that output.
4. Once the current settings appear, change Spatial audio or Equalizer, or
   choose Game, Movie, Music, Voice, or Custom. An observed Default preset is
   retained so its exact settings can be restored.
5. Adjust the selected preset’s EQ bands, then choose **Apply EQ**.
   **Reset to factory** stages that preset’s original curve; **Flatten** stages
   zero gain. **Discard changes** returns to the last confirmed curve.
6. Open **Enhancements** for Voice clarity and speaker Sound normalization.
   Turn an enhancement on and adjust its level. Changes apply after a drag
   finishes or a short pause in keyboard changes. Each row shows its level.

RZ09-0581 speakers have seven EQ bands. The rznb11 models and all headphone
outputs on the three V4 models have ten. All bands support -12 to +12 dB.
Editing Music keeps Music selected; Custom is also available as its own preset.
An observed Default or application preset must be changed to one of the five
EQ choices before editing. Preset selection applies the complete saved curve.
Application-specific THX
profiles are separate from the five EQ choices.

Windows must allow audio enhancements for THX to affect playback. If Windows
audio enhancements are off, select **Device Default Effects** under Windows
**Settings > System > Sound > Speakers > Audio enhancements**. OpenBlade shows
a warning when it detects this bypass on the selected default output. It does
not change that Windows setting. The warning refreshes when settings are read.
A confirmed THX switch reports the stored THX setting; it does not measure the
resulting sound.

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

Application routing is available on the three V4 models. The rznb11
models use the same reviewed executable map and routing operations; their
physical automation behavior remains unvalidated.

Automatic changes target only the recognized Windows default Realtek output.
An external, missing, or ambiguous default output leaves THX unchanged.

Reading settings connects to the THX component. Enabling the integration,
starting OpenBlade, changing the output selection, and closing OpenBlade do
not apply audio settings unless saved audio automation was explicitly enabled.
Turning the integration off stops automation and leaves the current audio
settings in place.

Opening the THX page connects automatically when the integration is enabled.
Read settings appears only after a failed operation, when an explicit read is
needed for recovery. Loading replaces the status message in the same area.
Automatic connections never clear an uncertain-write pause. If a change cannot
be confirmed, use **Read settings** before trying another change. OpenBlade does not repeat an uncertain command. If the supported
components are unavailable, the audio controls remain disabled. The explicit
setup action can provision the supported modern stacks with Synapse absent.
Uninstalling Synapse from an existing installation may remove shared components;
that retention path has not been tested.

## Report a detection problem

If THX cannot connect, the THX Audio page shows the detection failure below
the status message. Component failures name the missing or incompatible native
DLL, driver package, configuration file, or service.

If the native THX component is missing, select **Download THX component**.
OpenBlade downloads the reviewed file directly from Razer and checks its size,
SHA-256 and Razer signature before saving it. After the download, select
**Read settings** to connect. A failed or canceled download leaves an existing
component unchanged. The download does not change audio settings.

This client download removes the need for Synapse's copy of the DLL; it does
not install the THX audio driver, service or model-specific configuration.

Choose **About > Export diagnostics** and share the saved ZIP when reporting
the problem. Its `thx-audio.json` entry records a fresh detection result and
failure detail, even when the integration is off or the THX page has not been
opened. If the Session Agent cannot be reached, the entry records that query
failure instead. Exporting checks installed components without loading the THX
native client or changing audio settings.

## Install missing THX audio components

On RZ09-0528 or RZ09-0530, select **Install THX audio** after a component check
fails. OpenBlade verifies the client download for the current user, then asks for
administrator permission to install the matching rznb11 driver and configuration
from Razer. Setup may interrupt playback and may require a Windows restart.
It does not install Synapse or change the selected THX effects or presets through
the audio-control API.

This setup path is for a fresh THX installation with the exact supported codec
and the matching base audio drivers described above. Connect AC power and close
Synapse and
THX Spatial Audio first. OpenBlade refuses to replace or repair an existing,
partial or mismatched THX stack. The pristine-state requirement prevents the
vendor installer and configuration updaters from overwriting existing THX user
settings. It is an installation safeguard, not a prerequisite for ordinary THX
controls.

After setup, select **Read settings** to connect. If setup requests a restart,
restart Windows, then select **Install THX audio** to finish only the remaining
steps. OpenBlade requires a recorded successful restart request, a new Windows
boot identity and fresh stack verification before continuing; it never repeats
the submitted step. If its result cannot be confirmed, stop and export
diagnostics; OpenBlade keeps a durable installation record and does not replay
an uncertain operation. It does not restart Windows automatically or remove
packages as an automatic rollback.

The helper accepts one fixed operation from the installed Session Agent. It
checks the exact host in both registry sources, USB and codec identity, current
session, AC power and competing controllers. Downloads have pinned sizes,
SHA-256 and THX Authenticode signer identities. Files remain locked through
verification and process execution in a protected staging directory. Every
vendor process is preceded by a flushed transaction intent. Success requires
independent driver hashes, Windows catalog trust and membership, immutable
configuration hashes, and the running service's exact path; a vendor exit code
alone is insufficient.

The base rznb11 installer runs with the vendor's `/s /norestart` arguments. The
properties and surround configuration updaters take no arguments, deploy fixed
outputs and restart their THX service; their ignored service-command results are
checked independently. This source-backed sequence has simulated failure tests
but has not been installed on RZ09-0528 or RZ09-0530 during development. It remains
experimental. No rznb11 package may be substituted for rznb12.

## Component and process boundaries

Admission pairs each exact model with its own product ID, signed-INF audio
hardware IDs, service, and complete component hashes. It requires matching
host names in both registry sources and the model's present USB identity.
DriverStore directory suffixes are discovered; complete package hashes and
the running service path must match without mixing directories or versions.
Admission also checks the immutable properties archive and surround config in
`ProgramData\thx\{productId}`. It does not hash mutable property values or
preset selections, so normal user changes do not invalidate admission.
For native reads and changes, the isolated helper performs full fresh admission;
the dispatcher does not repeat those same scans. Discovery still performs full
admission without loading native code. The DLL hash is checked again with the
file locked through native loading to prevent replacement between verification
and loading. Full native readback and pause-on-uncertainty remain unchanged.

All three admitted models use the pinned `ThxV4Native_v1.1.1.0.dll`. The rznb11 middleware
ABI and typed property operations match the reviewed rznb12 implementation;
factory speaker curves remain model-specific. A new component build needs
its own source review and admission. The cached rznb12 1621 build is a separate
whole-stack exception with the same pinned 1846 active configuration and native
client. Before its first mutation, the helper must successfully read both outputs,
including every exposed property and preset list, for that exact stack identity.
The original 1621 properties archive alone cannot admit control. Static contract
comparison supports this experimental pairing; it does not prove native success
or audible processing on the older build.

The explicit download uses the reviewed
[Razer native client URL](https://app-assets.razer.com/files/synapse/devices/common/THX/ThxV4Native_v1.1.1.0.dll)
and pins its 13,684,872-byte length, SHA-256
`8E8F6923624762935D70B72F58BEC00808DB84DF740340047919B53331130A0A`,
and Razer USA Ltd. signing certificate
`3FDDBD6F820144422494342EF44898D732E068DC`, alongside Windows Authenticode
trust verification. The
[catalog entry](https://github.com/OpenBladeProject/blade-driver-catalog/blob/89c12ae4d805a8102776f75b8e27d46084a137b9/components/thx-v4-native/1.1.1.0/component.json)
records the same evidence as metadata; neither
repository redistributes the DLL. Runtime pins are local and do not depend on
the catalog being available.

The catalog's applicability ledger reviews every supported OpenBlade model for
every driver stack and standalone component. Razer's manifests identify this
exact client for RZ09-0530, RZ09-0528 and RZ09-0581. The other four supported
models have no THX client resource in the reviewed manifests; their applicability
is recorded as not established. The DLL is shared middleware, while each model
still needs its matching audio stack. The existing catalog driver stacks also
retain their exact device IDs even where common dependency files are identical.

Adding a supported model requires reviewing all catalog entries and refreshing
the offline fixtures under `tests/OpenBlade.Tray.Tests/Fixtures`. Tests compare
the complete supported model list with that review and the runtime THX pins with
the catalog descriptor. Catalog tests separately reject missing artifact reviews
and mismatched applicability lists.

The downloaded client lives under
`%LOCALAPPDATA%\OpenBlade\Components\THX\1.1.1.0`. Admission prefers a verified
copy there and retains the verified Synapse-installed path as a fallback. Both
paths use the same exact-model stack admission and locked hash check before
loading. Discovery and startup do not initiate downloads. This change requires
a coordinated full installation because the Session Agent's resolver changes;
audio IPC is now version 2; update the Tray and Session Agent together.

The tray stores an additive per-user opt-in preference. It uses a separate
current-user/session Audio IPC endpoint, version 2. Saved audio uses profile
store schema 21, export schema 13, and service IPC version 68. Existing Display
and GPU IPC versions are unchanged. Each power profile limits its application-rule
list to 4 KiB of encoded JSON so all three profiles and maximum lighting settings
fit in a service status response. The editor reports when the rule list is full. This change requires a coordinated full
installation; it is not eligible for tray-only deployment.

The Session Agent serializes typed audio requests. One bounded helper process
retains an authenticated private connection to its verified parent and lazily
registers the two supported outputs. It loads the pinned native client on the
first read or setting change and reuses that connection for later operations.
Each request still performs fresh admission and native readiness checks; neither
audio state nor write permission is cached. Present-device and service checks use
SetupAPI and the Service Control Manager directly, avoiding WMI inventory queries.
The helper belongs to a kill-on-close Windows job before it receives work.
An unconfirmed helper exit blocks further operations until its exit is confirmed.
The native DLL stays mapped until the helper exits because its async
callbacks can outlive client unregistration. Windows provides this lifetime
through [module pinning](https://learn.microsoft.com/en-us/windows/win32/api/libloaderapi/nf-libloaderapi-getmodulehandleexw).
The client talks to the separately installed THX properties service, while
the Windows Audio APO performs audio processing. The audio-control helper does not open
the laptop control HID handle, launch Synapse, install drivers, or elevate.
The explicitly launched setup helper above is a separate fixed operation.
OpenBlade's existing hardware ownership and competing-controller checks are
unchanged.

Discovery checks exact host identity, present USB identity, and the pinned
installed components without loading the native DLL. Each operation repeats
admission. Successful setting changes require matching readback. A timeout,
crash, or ambiguous mutation pauses further changes until the helper has exited
and an explicit read reconciles the state. A failed request retires the helper
without replaying the request. Normal disposal closes the private pipe, unregisters
the outputs, and bounds shutdown with a termination fallback. Turning off the
integration stops requests; the bounded idle helper remains until Session Agent
shutdown. Neither registration nor cleanup changes audio settings.
Optional foreground automation
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

## Read-only warm connection validation

On 2026-10-16, a developer runner exercised the production helper client,
parent authentication, job containment, admission, host, and native adapter on
RZ09-0581 with Synapse closed. Its wrapper rejected both native mutation methods.
Seven sequential requests read speakers four times, headphones twice, then
speakers again. Every response succeeded and repeated endpoint states matched.

The first speaker read took 851 ms. Later speaker reads took 141, 161, 174,
and 157 ms. First headphone registration took 217 ms; its next read took 153 ms.
These measurements include fresh admission and complete native readback. The
helper exited after disposal, with no surviving runner process. No setting or
preset mutation was sent. Live write latency and a timed Synapse comparison
were not measured; writes share the retained helper and fresh admission path.

The vendor product-736 middleware retains registered endpoint objects for normal
get/set operations and removes them during termination. OpenBlade follows that
connection lifetime without copying the vendor's startup or shutdown EQ writes.
The inspected middleware SHA-256 was
`26022B8618DDE19E54A3B2C386E5CD16E1D62F036C05B01A1E53E8B6540F9888`.

## Remaining experimental validation

The RZ09-0581 reversible round trips above and the user's audible built-in
speaker spatial check remain the recorded physical evidence. Other outputs,
native behavior on RZ09-0528 and RZ09-0530, live known-foreground switching,
Immersive/Competitive selection, active-headphone output detection, speaker
normalization writes, and interruption recovery remain unvalidated. No broader
playback or audio-quality test was performed by the runtime runs.

These gaps keep the integration experimental and prevent a production-ready
claim; they do not block an experimental release with the limits stated here.
Any UI preview must use a disconnected, simulated backend and must not connect
to the installed Session Agent's THX endpoint.

The legacy backend uses audio IPC v2 to mark EQ as observed-only rather than
reporting a guessed switch value. A coordinated full installation is required;
tray-only deployment cannot update this contract. Catalog and runtime tests
require an explicit THX or non-THX review whenever a supported model is added.
