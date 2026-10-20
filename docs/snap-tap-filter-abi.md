# Additional SnapTap filter ABI review, 2026-10-20

Static-only PE/disassembly review. No driver was installed, loaded, opened or sent an IOCTL. Addresses below are image-relative virtual addresses (RVA). Package/release and INF evidence is maintained by the parallel exact-model package review; this report supplies the filter ABI evidence.

## Exact reviewed binaries

The KB/DKM/Mouse/VCon role copies in each signed package have the same per-model hash. Package URLs and hashes are pinned in the [vendor artifact report](snap-tap-vendor-artifacts.md).

| PID | Package directory | Filter version | SYS SHA256 |
| --- | --- | --- | --- |
| 0255 | PID0255Drv_v0.1.2.0 | 1.0.0.71 | 0533D4A3B83243959254E05BA45B7290B6FB6CF6CCCF4F71FF255EFF08BE55E2 |
| 029D | PID029dDrv_v0.1.1.0 | 1.0.0.73 | 556049CFA85B817A899D6D3EF5AF42C6D45F0198CA68CE69908C6D9FFA60070D |
| 029F | PID029fDrv_v0.1.1.0 | 1.0.0.73 | 97766397880C87B6C606E040C53EB3779A77EEA8CA62154C4C3C2C80FD401535 |
| 02A0 | PID02a0Drv_v0.1.1.0 | 1.0.0.73 | AD50A47698F0FA882B0A44F5CC898A1A8A1B8D9213BFDFF629CF49F63872D180 |
| 02C5 | PID02c5Drv_v24.0.0.0 | 1.0.0.78 | 597C281383D4C7F07A789E88DF92E7814DC7F8118AC5631D3BF4FE8E5E1A87EB |

029D/029F/02A0 have byte-identical executable sections, independently recomputed: .text SHA256 f278d3c07af095b20230e4bc4ebac4058f9b5af049c77e292d775e07bcf40de5; PAGE 9631577f22b32d52ea3daf4e405d6d722e847dc700ec403c46b067ced3baa7fd; INIT f8fee12be6aa8464bb8a7d29fe1b412dc6050d54149c78ec897ef4dcb89580ad. The .73 column therefore applies to all three exact pinned binaries.

## Function map

RVAs in this table are hexadecimal.

| Contract | 0255 .71 | 029D/029F/02A0 .73 | 02C5 .78 / reviewed 02C6 .78 |
| --- | --- | --- | --- |
| CONNECT_DATA interception | 210C | 2098 | 2078 |
| Physical keyboard callback wrapper | 2454 | 2450 | 2430 |
| Physical keyboard handler | 33DC | 3680 | 36C0 |
| Device identity association | 1458 | 1198 | 1198 |
| Control interface vtable | 7170 | 9280 | 9298 |
| Register callback, vtable +18 | 18B8 | 1630 | 1630 |
| Cleanup, vtable +28 | 1320 | 1000 | 1000 |
| Input, vtable +30 | 1BC8 | 19B0 | 19B0 |
| Original-output bypass, vtable +38 | 1F04 | 1DF0 | 1DD0 |
| Type-2 input wrapper | 1D04 | 1B30 | 1B10 |
| Type-2 output wrapper | 1F44 | 1E30 | 1E10 |
| Control device creation / interface publication | 4134 | 4974 | 49B4 |
| Query-interface GUID data | 70F8 | 9208 | 9218 |

In .73 and 02C5, the ten reviewed functions for connection, wrapper, handler, association, registration, input, original output, both wrappers, and control creation have the same normalized instruction sequences as reviewed 02C6: instruction mnemonic, operands, non-address immediates and structure offsets preserved; branch/call target addresses and RIP-relative address displacements normalized. This is a bounded function comparison, not a claim that entire images are identical. Direct callback targets and vtable slots were separately resolved as above.

## Shared contract

All versions save original physical CONNECT_DATA at device+468+type*16 and active forwarding at device+548+type*16. Ordinary keyboard type is 2, giving original context/function +488/+490 and active +568/+570. The keyboard callback resolves its own WDF device/context, then dispatches through that physical instance's vtable +30. The separate type-12 path remains distinct.

Registration associates a control instance with its matching physical devices by identity, with per-type physical pointers at control+28+type*8. The incoming callback/context pair is at control+98+type*16. Registering type 2 replaces that physical instance's active forwarding with the control device context and the type-2 input wrapper, and returns the type-2 output wrapper/context. Input calls the registered callback if present, otherwise the saved original callback. Output always uses the original class callback at control+180+type*16 and original context at control+178+type*16; it does not re-enter the registered input hook.

Null callback registration restores the original physical CONNECT_DATA. Control cleanup checks each association is still live, restores original +468+type*16 into active +548+type*16, and clears dead associations. In .71 these are registration18B8 and cleanup1320 (restore at137F/1387); in .73/.78 cleanup1000 (restore1082/108A). These are per-control/per-associated-device operations, not a process-wide keyboard hook.

All versions publish query interface GUID {14CC0596-CFA5-4379-A5AB-FA24E681D040}, size0x28/version1. .71 sets size/version at45AC, references GUID at45F5, and registers at4607. .73 sets size/version5044, GUID5025->9208, registers5066. 02C5 sets size/version5084, GUID5065->9218, registers50A6. This is the interface consumed by the pinned common driver's query routine2B40. Its callback registration5104 uses type2, callback5680 and the common device context through slot+18, saving the returned output function/context at common+70/+78.

The .71 compiler layout differs materially in instructions (direct indirect calls and WDF table globals instead of the newer guarded-call plumbing). Its physical keyboard handler33DC also initializes InputDataConsumed to zero when non-null before forwarding. These differences do not change the reviewed callback record, keyboard type, original-output bypass, association, or restoration ABI.

## Admission and limits

The existing shared notification/output/hook codec and subscriber/owned-hook cleanup can be reused only with the separately pinned RzCommon.sys 1.0.0.76 SHA256 64B72EF9CE409BB17D31096C119B68C4A6A02AB35B7535D763157A4E1A587E59. Model filter packages alone do not establish that this common driver is installed. Its whole-file identity preserves the existing IOCTL record sizes, per-device hook-table behavior, per-file subscriber aggregation and output completion semantics; filter ABI review does not broaden that pin.

Keep model profiles responsible for exact host name, VID/PID, filter service/name/hash, control hardware identity, and exact keyboard collection/container/ancestry admission. Keep common code responsible for the typed A/D codec, subscription supervision, owned canonical-hook restoration, and stop-on-ambiguous-output recovery. Restore only owned records, never reset another subscriber's aggregate state or guess a key-up after uncertain submission.

This establishes source-backed experimental compatibility for these five pinned filters with the pinned common driver. It does not prove physical A/D behavior, hot-unplug, suspend/resume or actual installed topology on these laptops. Do not inherit 02E0's physical validation or installer authority.
