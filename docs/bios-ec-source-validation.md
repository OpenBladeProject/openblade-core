# Blade 16 BIOS and EC source cross-check

Reviewed 2026-11-07 against core commit
`d49eb99a6175291fbbf5abfa1a0fac1a3e93b08e`.
The [sanitized evidence record](../evidence/firmware-source-review/2026-11-07-blade16.json)
contains the complete carrier/member SHA-256 values, extraction offsets and
32 derived factory fan-table summaries. No vendor binary is committed.

This is a bounded static review of exact RZ09-0581 and RZ09-0528 firmware,
not a new physical validation or a complete review of every firmware command.
Existing runtime admission and per-feature physical evidence remain authoritative.
No updater, extracted firmware, ACPI method or firmware helper was executed;
no device access, installation, flashing, firmware mutation or sleep test occurred.

## Inputs and method

| Model | Component | Input | Status |
| --- | --- | --- | --- |
| RZ09-0581 / `1532:02E0` | BIOS 4.01 | `SO6120_BIOSv0401.exe` | Existing local carrier; Razer USA Ltd. Authenticode Valid |
| RZ09-0581 / `1532:02E0` | EC 1.04 | `SO6120_ECv01.04.exe` | Existing local carrier; Razer USA Ltd. Authenticode Valid |
| RZ09-0528 / `1532:02C6` | BIOS 2.02 | `SO6110_BIOSv0202.exe` | Downloaded from the exact official article; Razer USA Ltd. Authenticode Valid |
| RZ09-0528 / `1532:02C6` | EC 1.09 | `SO6110-ECv0109.exe` | Downloaded from the exact official article; Razer USA Ltd. Authenticode Valid |

The official model indexes are [RZ09-0581 downloads](https://mysupport.razer.com/app/answers/detail/a_id/20854)
and [RZ09-0528 downloads](https://mysupport.razer.com/app/answers/detail/a_id/19821).
The 0581 component articles are [BIOS](https://mysupport.razer.com/app/answers/detail/a_id/20847)
and [EC](https://mysupport.razer.com/app/answers/detail/a_id/20850).
The 0528 [BIOS article](https://mysupport.razer.com/app/answers/detail/a_id/15235)
links `https://rzr.to/2vxhvx`; its [EC article](https://mysupport.razer.com/app/answers/detail/a_id/21226)
links `https://rzr.to/58kmn2`. Resolved official CDN URLs are pinned in the evidence.
This review does not claim a fresh download comparison for the retained 0581 carriers.

NSIS data blocks were decompressed offline with standard-library LZMA.
A carrier block offset names the four-byte stored-length field, before the
compressed member. BIOS data was parsed with UEFIExtract NE alpha 74 and
ACPI tables checked for bounded declared length and an eight-bit zero checksum.
ACPICA iASL 20250807 disassembled the selected tables without evaluating them.
The retained 0581 BIOS extraction first reconstructs each AMI PFAT component;
parsing its interrupted compressed stream as one ordinary volume is insufficient.
Ghidra 12.1.3 recovered selected code; pseudocode is an analysis aid, not vendor C.

## RZ09-0581: normal thermal policy corroborated

EC image `SO6120_EC_0104.bin` is 262,144 bytes,
SHA-256 `8537e9fd55b7b35504d8d87b0191acc4391b67365b3fe72f6e1029a424c9e707`.
Its identity at image offset `0x90` is `ITE8320EC-V01.04`.
The retained NDS32 instruction listing establishes these selected paths:

- `0x15E46` handles normal selectors `0..7`, separately from internal `FE/FF` paths.
- The ordinary manual clamp at `0x15C62..0x15D9C` multiplies requested bytes by
  100 RPM. At `0x15C88..0x15C9E`, selector 3 alone maps below-minimum requests
  to zero; other normal selectors use the minimum.
- Loader instructions `0x16406..0x16418` and `0x16432..0x16444` read minimum
  and maximum fields at table-extension offsets `+0x21` and `+0x26`.
- A fresh independent decode checked four board groups at `0x3B000`,
  `0x3C000`, `0x3D000`, `0x3E000`, each with eight 512-byte records.
  All 32 records (344 stages) contain a first-stage target of 0/0 RPM and
  ordinary manual minimum/maximum fields of 2,000/5,400 RPM.
  Selector 5 records are at `0x3BA00`, `0x3CA00`, `0x3DA00`, `0x3EA00`.

Human-readable mode names come from the separately reviewed exact-product
protocol. The firmware records contain numeric selectors. These findings
corroborate ordinary mode-specific fan policy and explain why retaining a low
requested manual value does not establish the same effective fan target.
They do not justify changing the requested-value contract or adding new modes.

The factory table is not a read of the active persistent table. Valid persistent
records can replace defaults. Multiple input channels, hysteresis, dwell and
other state gates affect stage selection. In particular, `0x15030..0x15068`
can raise decoded targets to at least 3,000 RPM when either of two separate
state flags is set. A zero target is not measured fan rotation.
Internal duty branches and generic EC mailboxes do not establish a supported,
bounded host control with safe ownership and exact restoration.

## Both models: thermal-zone name does not prove CPU temperature

The reviewed DSDTs contain these exact names. At the reviewed baseline,
`SafeWindowsFanTemperatureSource.WindowsThermalZone.RazerEcCounterPaths`
(`src/OpenBlade.Service/FanCurveControl.cs:394-400`) listed both paths:

| Model | Thermal zone | `_TMP` name offset within AML |
| --- | --- | --- |
| RZ09-0581 BIOS 4.01 | `\_SB.PC00.LPCB.EC0.TZRZ` | `0x370AA` |
| RZ09-0528 BIOS 2.02 | `\_SB.PCI0.SBRG.EC0.TZRZ` | `0x91BB` |

Both ordinary ACPI `_TMP` methods return the literal `0x0C6E`:
3182 tenths kelvin, approximately 45.05 degrees Celsius. The same literal is
present in the retained live 0581 DSDT. The retained live table and BIOS image
table have different hashes; they are not represented as byte-identical tables.
A complete dynamic CPU-temperature source is therefore **not established by
these thermal-zone definitions**. A counter's path or plausible numeric value
alone cannot establish the sensor's meaning or freshness.

The 0528 DSDT is the checksum-valid 40,255-byte raw section under FFS GUID
`C118F50D-391D-45F4-B3D3-11BC931AA56D`, SHA-256
`aa91c2da04dd656016958e785ed6f1e2f41ecd222e5d7cca1e906fcc775e6521`.
It declares `CPUT`, `CP2T`, `GPUT` at EC offsets `0x56`, `0x57`, `0x58`, and
a 32-bit `ECVR` field at `0x20`. Those declarations do not independently
establish sensor calibration, a supported host acquisition route or live values.
Its `_TZP` returns `0x1E`, and its critical/hot trip-point expressions are
separate from `_TMP`.

The 0528 `RazerAcpiDxe` member (FFS
`48E0B361-F517-41E2-870C-73C84475B52C`, 7,680 bytes) was also inspected.
Recovered entry RVA `0x17EC` uses helper `0x1B50` to request EC data selectors
`E8/D0` and populate NVS. This bounded review did not establish a dynamic CPU
getter or an `_TMP` repair from that code. It is not an exhaustive absence proof
for every boot/runtime patch or Windows counter implementation.
The associated software fix removes both paths from the shared CPU-temperature
reader. No replacement sensor or numeric 45-degree blacklist is introduced.
When CPU temperature is unavailable, the existing custom fan-curve calculator
selects the model's admitted curve maximum: 5,400/5,400 RPM on RZ09-0581
and 5,000/5,000 RPM on RZ09-0528, including when the GPU is inactive. Automatic firmware fan handling
is a separate path. Focused software tests cover null CPU readings and that
existing fallback; no live PDH sample or physical fan response was tested here.

## RZ09-0528: exact firmware, separate command evidence still required

BIOS `SO6110_202.rom` is 33,554,432 bytes, SHA-256
`5bbd09cda602e8555b04d7ca2a3a7bcbbdfc929be8a7bc5c81e6c3dfc7cbb2f4`.
The static flash-script name and updater configuration bind it to SSID
`1A58300E`, family `RZ09-0528`.
EC `SO6110_EC_0109.bin` is 262,144 bytes, SHA-256
`44dc4e4c92ea4be9def93670a95b2bf859cb648b8dfe393fb5877735debbef5d`.
Its header at `0x90` names `RTW--81202-01.09`; `0x2D00` names
`RTW81202 PI480`. The updater declares SSID `1A58300E` and five cases,
`RZ09-05285` through `RZ09-05289`.
These are updater provenance, not new runtime SKU admission restrictions.

Reset-vector bytes are consistent with compressed RISC-V, unlike the 0581
NDS32 image. No 0581 offsets, tables, MCU behavior or policy assumptions are
transferred. This review did not reconstruct the 0528 EC fan tables, manual
clamp, normal HID-to-EC bridge, battery controls or keyboard-mode semantics.
Its existing physical/source-reviewed command evidence is not replaced by a
matching package filename, version header or thermal-zone name.

## Result and remaining scope

The images corroborate selected 0581 thermal semantics and exact BIOS/EC
provenance for both models. They also identify a concrete limitation in using
their ordinary ACPI thermal-zone definitions as proof of CPU telemetry.
No additional firmware command, model capability, electrical GPU power-off
claim or physical-validation status follows from this review. Battery,
keyboard, lighting, lifecycle recovery and other controls retain their own
reviewed contracts and evidence. The accompanying shared telemetry fix rejects the two constant-backed paths;
it adds no firmware write or temperature-source admission. No overheating or
physical thermal outcome is inferred from this static finding.
