# RZ09-0328 / 0255 Microsoft Catalog keyboard API review

Reviewed 2026-10-26. This is a static, exact-input review of the Microsoft
Update Catalog package containing `RzDev_0255.sys` and `RzCommon.sys`, both
file version **1.0.0.66**. It establishes the bounded keyboard API used by
OpenBlade's existing A/D-only SnapTap implementation. It does not establish
physical installation, input timing, gaming compatibility, or whole-driver
security. No vendor binary was executed, installed, or loaded during review.

The reviewed pair supports the existing typed keyboard contract. Its internal
layouts differ from the previously reviewed filter .71/common .76 pair; those
internal offsets must not be used as a runtime ABI or copied across builds.
This source package can support a separately pinned, source-backed experimental
0255 installation recipe, subject to the existing exact-host/topology, catalog,
installation readback, subscription, ownership, and recovery requirements.

## Exact source and package closure

The source is the [Microsoft-hosted 0255 driver CAB](https://catalog.s.download.windowsupdate.com/c/msdownload/update/driver/drvs/2020/06/ca80e851-41d4-4bd0-b402-a71b3b129640_93a472b0e28e359b092a7bd82a4d1206274086b0.cab).
Its size is **55,305 bytes**, SHA-256
`7CCECED892A74614A56AFEC9591B8238DB22298BE21137C33D99D3F7FD44BAA0`.
The CAB contains the following complete flat 12-file package. No files from
02C6, 02E0, or the Razer .71 carrier are required.

| File | Bytes | SHA-256 |
| --- | ---: | --- |
| RzCommonU.inf | 1629 | AE6E2C7C8CD98A281AE0B7E5AC66F4C0F1BD62F384BC78069E1F63D7597D5DB2 |
| rzcommon.cat | 10511 | 9B365FE76FA4CFB02CF0BC46BFADB52A5F3EAF58E633321911C483100462B9A9 |
| RzCommon.sys | 51592 | 56A4850AE97AD3A9F786601445C7EAA70B645BB92128510D56A5CC5D63084EAF |
| RzDevU_0255_Dkm.inf | 1813 | 8FC6176EAC81EE02FBC34673AEDA711879C9064519A84490BA414FF916FC0F58 |
| rzdev_0255_dkm.cat | 10543 | 3159EB2B44A34D86093802E6458A77A93009BE9E81BC4E8808DB9141F9380D28 |
| RzDevU_0255_Kbd.inf | 1960 | 20F9EC60BE696C5CC87ABADFD639CE017D1DBD33C91CCCC9CBE6FD681ACF5C92 |
| rzdev_0255_kbd.cat | 10543 | 8A94915CF7D5D5201FF2CA443294D0BB0D6620AF180626FCAC11555DA375B261 |
| RzDevU_0255_Mou.inf | 2007 | 391875B12009BADFEBD2DD5E2EA893E638752CA9D52FA2EB97AD55652BC7CF78 |
| rzdev_0255_mou.cat | 10543 | A701E237D4C59AC84B6F6F6E850D3BF9225B7F52DA885A24F6B755FE505793D5 |
| RzDevU_0255_VCon.inf | 1850 | F9630068A42BF1866A406F85EC7FECD294B6530DD0EFB52591798988FF030067 |
| rzdev_0255_vcon.cat | 10547 | 917AE75A83DDB9C84BE4A29F86B92F03CA588AC32D20740E1C72062EA2C09D44 |
| RzDev_0255.sys | 52104 | 525613053F0155B2627E1CC83DD155D554AECD9AC84E2A473E3AD64008300E4C |

All five catalogs returned Authenticode `Valid`, with the Microsoft Windows
Hardware Compatibility Publisher certificate thumbprint
`2BA2AECC0109C312674793FFC1FE03F757B75B21`. Their catalog `OSAttr` is `2:10.0`.
Independent package review verified every declared INF/SYS member with SignTool
with explicit current-OS policy (`/o 2:10.0.26100.0 /c <catalog> <member>`). These are acquisition provenance
and current-policy checks, not a runtime build or certificate allowlist.

The separately reviewed Razer-carrier .71 legacy catalogs advertised only
Windows 6.1/6.3 and failed current-OS catalog policy with an OS mismatch.
They must not be substituted for these catalogs. Matching filenames or a
valid standalone signature cannot establish current catalog membership.

The signed U VCon INF targets
`RZVIRTUAL\Vid_1532&Pid_0255&MI_00&Col03`, associates `RzDev_0255`, and declares
the Windows inbox `mshidkmdf` upper filter and service at
`%10%\System32\Drivers\mshidkmdf.sys`. This U package has no WDF coinstaller
closure. The older legacy INF/coinstaller requirements do not apply to this
different signed package; signed INF directives must remain unchanged.

## Method and address convention

The two PE files were parsed and disassembled offline with `pefile 2024.8.26`
and `capstone 5.0.6`, installed only in a disposable analysis directory.
Addresses below are hexadecimal RVAs, not raw file offsets. Image base is
`0x140000000`. Functions were located from PE exception-directory entries;
call paths, data references, vtables, and buffer accesses were inspected.
Similarity ranking assisted navigation only; it was not accepted as proof.

Comparison inputs were the previously reviewed `RzDev_0255.sys` .71,
SHA-256 `0533D4A3B83243959254E05BA45B7290B6FB6CF6CCCF4F71FF255EFF08BE55E2`,
and `RzCommon.sys` .76,
SHA-256 `64B72EF9CE409BB17D31096C119B68C4A6A02AB35B7535D763157A4E1A587E59`.
See [filter ABI review](snap-tap-filter-abi.md) for the earlier contract.

## Filter and common-driver association

The .66 filter's query-interface GUID at RVA `7128` is
`{14CC0596-CFA5-4379-A5AB-FA24E681D040}`. Control creation `3E34–44D5`
declares `RAZER\DeviceContol` (vendor spelling), installs the vtable at `7188`,
and writes interface size `0x28`, version `1` at `437A`. Common .66 queries
that exact GUID/size/version at `2224–2267`; `42D0–4388` retains the returned
interface. Applicability therefore follows the exact package and its actual
filter-created control interface, not a common binary borrowed from a model.

The filter vtable's relevant entries are registration `1584` at slot `+18`,
cleanup `1000` at `+28`, input `1890` at `+30`, and original output `1C1C` at
`+38`. Association `1148–13E9` matches physical identity to the control's
identity and stores each physical association at `control+28+type*8`.
For keyboard type **2**:

- CONNECT_DATA interception `1E58–1ECB` saves the original callback/context,
  rejects a duplicate connection with `0xC0000043`, and installs wrapper
  `20A8`. The physical handler `30BC` calls the active pair at `+538/+540`.
- Registration `1584–1729` chooses type-2 wrappers `19DC`/`1C60`, retains the
  registered pair in the associated control, and returns the original-output
  wrapper/context. A null registration restores the physical original pair
  from `+488/+490` to active `+538/+540` (`16EE`, `16F7`).
- Input `1890` calls the registered pair when present, otherwise the saved
  original. Output `1C1C` calls the saved original directly. The output path
  does not re-enter registered input.
- Cleanup `1000–10B5` restores the original physical pairs for live associated
  devices. Association/removal also restores pairs before detaching them.

Common registration `4388–457D` first unregisters existing callback slots
through null registration, then registers keyboard callback `45E0` for type 2
and retains the original-output pair at `common+70/+78`. The callback validates
the input range, initializes `InputDataConsumed`, and calls `2E74`.

## User-mode API and bounded responses

Common .66 dispatch is `24E4–2E71`. Sizes below are hexadecimal byte counts.
These match `RazerSnapTapDriverProtocol`'s existing codecs.

| IOCTL | Contract checked in .66 | Evidence |
| --- | --- | --- |
| `88883024` set hook | input at least `124` | check `263B`; set `5F00` copies the full record |
| `8888302C` clear hook | input at least `20` | check `2C15`; clear `505C` |
| `88883030` enumerate | input at least `4`, output at least `124` | checks `2B70`, `2B7E`; `534C` selects the indexed record; complete `124` copy |
| `88883034` enable notification mask | input at least `4`; applies to requesting file | check `2AF4`; file mask `4924`, aggregate `4B7C` |
| `88883018` receive notification | output at least `130`; pending per-file request | check `28E1`; enqueue `496C`; complete/copy `4C5C` |
| `88883020` send output | input at least `20`; type 1 keyboard record is `C` bytes at input `+8` | check `26C1`; keyboard branch `27C1–27DF` calls original output `3B24` |

The additional query `88883028`, unused by the current backend, also requires
input `20` and output `124` and copies the complete record. Undersized requests
take the dispatcher's `0xC0000001` failure path. Enumeration beyond the record
count returns `0xC0000001` (`537C`), matching existing end-of-enumeration handling.
Pending notification submission returns `0x103`; completed output size is
exactly `130`. Unsupported output types return `0xC0000010`.

Hook state belongs to the common device's table at `+540`. Set `5F00` allocates
`128` bytes, copies `124`, clears internal flags at `+124`, and updates the
collection under its lock. Matching `56B8` compares record type, keyboard scan
at `+A`, flags at `+C` with the up/down bit excluded from identity (`FFFE`),
and modifiers at `+10`. Press/release state is tracked separately. Clear
`505C` uses the corresponding identity. These operations support the existing
A (`1E`) and D (`20`) records without adding a new hook type or raw command.

Keyboard processing `2E74–3036` walks 12-byte input records. `5CA8` constructs
notification kind 2 with the complete saved hook record and current input,
then calls subscriber fanout `4F08`. A negative hook/delivery result forwards
that input through original output `3B24`; inactive interception forwards the
whole range. `InputDataConsumed` counts the processed input records. Original
output uses the saved pair `+70/+78`, with its own bounded output queue.

## Subscriber lifetime and restoration limits

Device initialization assigns file creation `15E4`, and **both** cleanup and
close to `15B0` (`B0D5`, `B0E1`, `B0ED`). File creation `49D4` initializes a
file-specific notification mask, ring, and manual request queue, then adds
the file object to the device subscriber collection. Notification IOCTLs use
the request's own file object; they do not select another subscriber's queue.

Cleanup/close follows `15B0 → 48FC → 4FF0`: remove that file from the subscriber
collection under the same lock used by fanout, then clear its retained file
handle. Repeated cleanup is harmless. Fanout `4F08` only enumerates remaining
files and returns failure if none accepts the event; keyboard processing then
forwards the input to the original callback. `4C5C` copies exactly `130` bytes
to a request obtained from that file's queue and completes it with that size.

Closing a subscription is **not** proof that the shared device hook table was
cleared, or that its aggregate enable flag was recomputed. Another enabled
subscriber can still accept events. OpenBlade must retain explicit restoration
of its owned hook records, competing-controller checks, supervised request
cancellation/handle lifetime, and uncertain-output recovery. This review does
not replace those existing safeguards with an assumption about close.

## Real differences and remaining validation

- Filter .66 accepts types below `B` (11), whereas .71 accepts types below
  `E` (14). Keyboard type 2 and its public callback contract are unchanged.
  Control/physical offsets shifted; they are private implementation details.
- Common .66 has an inline **256-slot** notification ring (`4C5C`, `4DC4`),
  whereas .76 uses a separately allocated **1024-slot** ring (`5DD8`, `5F24`).
  Both discard the oldest entry on overflow. Source equivalence does not imply
  equal buffering headroom or lossless delivery. Keep the existing bounded,
  supervised consumer and recovery behavior; physical stress behavior remains
  unvalidated.
- Common .66 and .76 retain the same relevant per-file mask, subscriber removal,
  full response copy, keyboard consume/forward, and original-output semantics.
  The reviewed .76 counterparts include keyboard `3BBC`, output `4734`, removal
  `592C/61AC`, and fanout `60A4`. Later code contains additional event types,
  different storage, and compiler mitigations; this review makes no whole-driver
  equivalence or security claim.
- Installation and SnapTap on RZ09-0328 remain source-backed experimental.
  Fresh package trust and exact binding/readback are required when installing;
  installed compatible updates remain governed by the API compatibility policy,
  not these download hashes. Physical validation must exercise the actual 0255
  stack, ordinary input passthrough, A/D operation, shutdown/restoration, and
  subscriber loss before claiming physically validated behavior.
