# Legacy THX 2.6.5 control interface

This is source-backed experimental support for the installed THX stack used by
2023 Blade laptops. It does not establish physical effectiveness or authorize
installation. Razer documents a separate [2023-and-earlier setup](https://mysupport.razer.com/app/answers/detail/a_id/5798)
using THX APO 2.6.5.0 and [THX Spatial Audio Control](https://mysupport.razer.com/app/answers/detail/a_id/3779).
The modern ThxV4Native client contract must not be substituted for this interface.

## Pinned primary artifacts

The Microsoft Update Catalog Windows 11 service package is update
[9a0052d8-8511-47b4-80d4-6dc4abf83d97](https://www.catalog.update.microsoft.com/ScopedViewInline.aspx?updateid=9a0052d8-8511-47b4-80d4-6dc4abf83d97).
Its THXRTSVC.inf declares DriverVer 06/17/2023,2.6.5.0 (line 6),
SWC\VEN_THX&PID_THXSVC (line 25), and VSSrv as an own-process service with
System32\VSSrv.exe and RPCSS dependency (lines 46-55).

| File | SHA-256 |
| --- | --- |
| VSSrv.exe | DC709A34D2CB043505B200316B4CA1A51B9AE1FC917B55DD804D5F30841C868F |
| VSSrvInit.exe | E2E15276061505C12A1540D4DDCF6788F1794C421385A43C6730DECD9679DCC0 |
| VSHelper.exe | B1708DCA72A4392A1B4005570CCD6CFE36E7AC5454A1B34CBB303FCE64BD22F0 |
| THXRTSVC.inf | 31428C1C6632C165A6DF0F0CE7EBBAAE1870F02F619E4D93B894B1F9BCCF918C |
| thxrtsvc.cat | 925B47DF9D2D9A3FE9F85D42CA27C1D1020E16043FA3B99081D521EDDC233063 |

The binaries have a valid THX LTD Authenticode signature with signer thumbprint
4BB8A04FDA0285FB9A7ACC812422559A120107C6. The catalog has a valid Microsoft
Windows Hardware Compatibility Publisher signature. These are package identity
checks, not proof of a particular laptop's codec or tuning.

The official [Store product metadata](https://displaycatalog.mp.microsoft.com/v7.0/products/9NC521C2JHDK?market=US&languages=en-US)
identifies THXLtd.THXSpatialAudioControl_2023.725.1.0_neutral_~_zgheytcpjsgqw.
The bundle SHA-256 is
2A35C2E5480235CBBBE0ABDF7CD904FBFFFDD3E8177705D7E0A010BD1ABEF221.
The x64 app's thxsahelper-discovery/thxsahelper-discovery.exe SHA-256 is
F64047C9C8CAD48BA863E1806A2EECD595FD4C9118DF70D564D73B2844830156.
Acquisition used Microsoft's public catalog/delivery metadata; inspection was
static. Neither the app nor a native vendor binary was executed or installed.

## ABI and initialization

VSSrv.exe's embedded TYPELIB is the authoritative ABI. Extracted TYPELIB SHA-256:
6FFBB03C6546E7CD4D2ACB3A71F8E0BB82E85FE057E1CF1DEC934788968311F8.
Its library ID is 1FD49704-F52F-4A60-80C6-E22723DEEBE2, version 1.0.
The service's embedded registration and strings identify AppID
EF809770-D328-4410-9772-32775E710711. REGISTRY resource 101 explicitly sets
LocalServer32 to %MODULE%, the AppID value to %APPID%, and the TypeLib to the
library ID above for both settings classes. Registration code at VA
0x140057bd0 supplies that AppID and writes the service-name buffer to LocalService;
VA 0x140054300 also creates the AppID key and initializes LocalService when its
launch permissions are absent. These native registration paths were inspected
statically and never invoked. Both interfaces derive from IUnknown,
use HRESULT returns and 32-bit integer booleans, and have the oleautomation flag.

| Purpose | CLSID | IID |
| --- | --- | --- |
| General settings | 087E4DB6-0519-4A63-9099-9201915E1371 | 392375FF-8204-4A26-B4C5-AF4B71ACA729 |
| THX settings | 2CCFC059-A1C5-4408-BCE5-0B23690DA7A2 | 3B3AF690-70A6-4DDA-BBEA-0B96493E9DA3 |

Slots below include the three IUnknown methods. The full declarations, including
unused slots, are preserved in LegacyThxAudioNativeAdapter.cs.

| Interface / slot | Signature |
| --- | --- |
| Both / 3 | Init(uint32 processId) |
| General / 13 | GetOutputDevice(out uint16 deviceId[80]) |
| THX / 4 | GetSpatialProcessingState(out int32 enabled) |
| THX / 5 | SetSpatialProcessingState(LPSTR originator, int32 enabled, out uint64 size, out byte message[4096]) |
| THX / 6 | GetProcessingMode(out uint32 mode) |
| THX / 7 | SetProcessingMode(LPSTR originator, uint32 mode, out uint64 size, out byte message[4096]) |
| THX / 10 | GetCurrentModeEQGains(out float gain[31]) |
| THX / 11 | SetCurrentModeEQGains(LPSTR originator, float gain[31], out uint64 size, out byte message[4096]) |
| THX / 13 | GetListeningMode(out int32 isHeadphones) |

In the pinned discovery helper, CVSTHXSettings constructor at VA 0x140011740
calls CoInitializeEx(NULL, 0), activates the THX class/interface and calls slot 3
with GetCurrentProcessId(). The general-settings constructor does the same.
The parameter is the calling process ID, not a processor, model, endpoint, or
codec ID. The THX constructor contains no prerequisite settings write. Broader
app initialization has other behavior and is not invoked by OpenBlade.

The spatial setter wrapper at VA 0x140012400 allocates 4096 bytes, initializes
notification size to zero, supplies its discovery-originator string, invokes
slot 5, and decodes the returned notification. OpenBlade supplies a unique ASCII
originator and bounds the returned size. Notification content is not independent
readback; no notification parsing or vendor discovery process is needed to call
the typed COM getter. The older thxsahelper.exe has a different setter signature
without the originator/notification parameters and must not define this ABI.

## Endpoint and admission boundary

GetOutputDevice returns the service's current UTF-16 endpoint identifier, not a
codec hardware ID. GetListeningMode reports whether the selected listening mode
is headphones. It does not independently establish that speakers are active.
The caller must match the exact returned endpoint against fresh Windows active
output enumeration and the admitted Realtek device's CM devnode ancestry.
A friendly-name match, a default renderer alone, or generic THX registry keys
cannot admit an endpoint. No setter changes output selection or listening mode.

Discovery must never construct this adapter. Explicit connection runs it only in
the isolated helper, synchronously on one owning MTA pool thread. A buffered pipe read may complete
synchronously on Main's STA thread, so the legacy request dispatch explicitly uses
Task.Run; ConfigureAwait(false) alone does not establish an apartment. The adapter hashes and
holds open the exact System32 VSSrv.exe, rejects relevant HKCU COM overrides,
requires the pinned HKLM CLSID/AppID/TypeLib/interface registrations, and admits
only the [OLE universal marshaler](https://blogs.windows.com/windowsdeveloper/2017/04/13/com-server-ole-document-support-desktop-bridge/)
through System32 oleaut32.dll. Unexpected paths, type-library resource suffixes,
service arguments, redirects, custom proxies, or surrogate registrations fail
closed. These exact registry forms have not been physically verified on a
factory 2023 installation; a differing legitimate form requires source review.

Activation uses CLSCTX_LOCAL_SERVER | CLSCTX_NO_CUSTOM_MARSHAL |
CLSCTX_DISABLE_AAA. It does not explicitly call an SCM start or restart API.
Fresh running-service/process checks bracket both activations. However,
[COM local-server activation](https://learn.microsoft.com/en-us/windows/win32/api/wtypesbase/ne-wtypesbase-clsctx)
can activate the registered service if it exits between the check and activation;
this is not a guarantee that no service launch can occur. Registry checks also
are not a security boundary against a concurrently hostile process running as
the same user or an administrator. The adapter's file pin prevents replacement
of the verified file while connecting. Parent admission additionally pins the
installed driver/APO components and exact host/codec before any control.

Any failed/canceled mutation may already have been submitted, including an
HRESULT error or oversized notification. Stop writes and reconcile through a
successful explicit read. Never replay or substitute a different setter. A
successful HRESULT alone does not establish the requested output state.

## Mode and EQ observations, not admitted controls

The pinned discovery helper's mode-name mapping at VA 0x140032b20 is:
0 Normal/game, 1 Grand, 2 Hyperlocator, 3 Music/default, 4 Cinema, 5 Voice,
6 Custom, 7 Flat Live, 8 Hip-Hop Club, 9 Pop Arena, 10 EDM Night Club,
11 R&B Lounge, 12 Rock Studio, 13 Acoustic Coffee Shop, 14 Jazz Cafe,
15 Classical Hall. These names do not map automatically to the modern presets.
The app falls back to mode 3 for unknown strings; OpenBlade must not guess.

The native float[31] is a buffer shape, not proof of 31 frequency bands. The
10-value getter at VA 0x1400145d0 selects native indices 3,6,...,30. The 10-value
setter at VA 0x140015f00 starts with a zeroed 31-value array and writes each input
i to indices 1+3i,2+3i,3+3i, leaving index 0 zero. The raw setter at
VA 0x1400152b0 passes all 31 values directly. No frequency or enabled-state
mapping is admitted from this alone. Spatial support reads only endpoint,
spatial state and listening mode; unused EQ/mode reads cannot block it.

Production legacy controls currently expose only the spatial toggle. EQ,
presets and application automation remain unavailable until their own semantics
and independent readback are established. SCU 2.6.5 embeds exact-model THX
acoustic profiles (PI490, SO690 and KI890), and selects them from the codec
subsystem ID on its normal branch. Fresh provisioning remains blocked until
original signed evidence establishes component creation, endpoint APO binding,
SCU branch and installation order. See the
[installation investigation](thx-installation-investigation.md#legacy-thx-tuning-and-binding-review).
