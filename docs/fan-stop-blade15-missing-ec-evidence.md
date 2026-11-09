# Remaining Blade 15 EC evidence

Reviewed 2026-11-08 against core
`c46ee058eff2a40b7ddbd82af3dac48c397d80b3`.
This extends the firmware search for RZ09-0300, RZ09-0301, RZ09-0328 and
RZ09-0351. **No complete exact-model Stop/Auto EC contract was established.**
The result is missing source evidence, not evidence that these models cannot
stop their fans. Physical validation is separate and is not the reason for
withholding a source-reviewed contract.

All vendor carriers and members were treated as data. No updater, vendor DLL,
EFI program or driver was executed; no device, ACPI method, installed service,
profile or hardware setting was accessed. Downloads, extraction manifests,
UEFI reports and bounded scans are ignored under
`artifacts/model-expansion/remaining-blade15/`.

## Official carriers recovered and pinned

All four downloaded carriers returned **Valid** Authenticode for
`CN=Razer USA Ltd., O=Razer USA Ltd., L=Irvine, S=California, C=US`, certificate
thumbprint `958B9D4B6D66B48FFC5B9FFC6B746113BF08EFBB`.

| Exact applicability in packaged `torden.ini` | Official carrier | Bytes | SHA-256 |
|---|---|---:|---|
| RZ09-03006 and RZ09-03009, SSID `1A582004` | [D3 BIOS 1.03](https://dl.razerzone.com/drivers/Razer%20Blade%2015%22%20Mid%202019%20-%20Base/RazerUpdater_v1.1.1.2_D3-BIOS0103.exe) | 6,594,088 | `51573bf4cc4d6424208d37478f42fd4902967a60610722f0c0e3ca2a7a5314ab` |
| RZ09-03017 and RZ09-03018, SSID `1A582005` | [C3 BIOS 1.04](https://dl.razerzone.com/drivers/Razer%20Blade%2015%22%20Mid%202019/RazerUpdater_v1.1.1.2_C3-BIOS0104.exe) | 6,797,448 | `3e513228ad02de6b7921a647f23129e7649aeb58bb600c944748f4b1c46e350d` |
| RZ09-03017 and RZ09-03018, SSID `1A582005` | [C3 BIOS 1.02](https://dl.razerzone.com/drivers/Razer%20Blade%2015%22%20Mid%202019/RazerUpdater_v1.1.1.1_BIOSv0102.exe) | 8,840,496 | `484ad0713f93462a00e48908f9b537a0b49ff62aac9d979cf22beaef6b41f3d1` |
| RZ09-0328, SSIDs `1A582011` and `1A58200C` | [DA550 BIOS 1.06](https://dl.razerzone.com/drivers/Razer%20Blade%2015%E2%80%9D%20Base%20%282020%29%20%7C%20RZ09-0328x/RazerUpdater_v1.1.1.2_DA550-BIOSv0106.exe) | 8,958,488 | `4c51547f99e13c164b1d0d5ddf1f164b31e6f1e4a98717f21f866ddfeef8b703` |

The original [RZ09-03006 BIOS guide](https://dl.razerzone.com/drivers/Razer%20Blade%2015%22%20Mid%202019%20-%20Base/Razer%20Blade%2015%20%28Mid%202019%29%20Base%20-%20BIOS%20Updater%20Guide.pdf)
contains hyperlink `http://rzr.to/CaOyK`: the third character is capital **O**.
Reading the PDF annotation recovered the carrier above. Search extraction had
rendered `CaQyK`, which instead redirected to Razer's 404 page. The actual
carrier independently binds both Base 2019 variants, so its relevance does not
depend on that transcription.

The [0301 BIOS article, answer 2803](https://mysupport.razer.com/app/answers/detail/a_id/2803/~/razer-blade-15%E2%80%9D-advanced-%282019%29-%7C-rz09-0301x---bios-updates)
publishes `wpnaI`, which resolved to C3 BIOS 1.04. The
[INTEL-SA-00233 article, answer 5245](https://mysupport.razer.com/app/answers/detail/a_id/5245/kw/the%20firmware)
publishes `BHfCJ` for these exact SKUs, resolving to C3 BIOS 1.02. Its Intel
security purpose does not establish EC contents or fan semantics.

## Complete carrier closure and BIOS inspection

The offline NSIS block parser checked complete block bounds and decompressed
every member: 15 blocks each for D3, C3 1.04 and DA550, and 18 for C3 1.02.
Offsets below refer to the stored-length field in the original executable.
Every listed BIOS member is 16,777,216 bytes.

| Carrier | BIOS member and block | Stored-length offset | BIOS SHA-256 | `torden.ini` block / offset |
|---|---|---|---|---|
| D3 1.03 | `DANA3_0103.bin`, 7 | `0x52AF2` | `759522008fa0dc6c54f34d935822f98caad7b780b26c3b519052bdd2bf47ec45` | 13 / `0x616169` |
| C3 1.04 | `Charlotte3_0104.bin`, 8 | `0x7B40C` | `71ef3cddb993905d251fc3f0555bc4601ba7f0540b9f1b30b76c1d5d815f547f` | 13 / `0x647BC5` |
| C3 1.02 | `Charlotte3_0102.bin`, 8 | `0x7B2E5` | `31857efb62f9485d054228ff6c8e7a65aa57b7776e32f974b60bece830e1e37d` | 16 / `0x83A86D` |
| DA550 1.06 | `Dana5_0106.bin`, 7 | `0x70BBD` | `7469c62dc7926e0dbd11064e2b017f2f10c241b9bf30392981a57f997d61639f` | 13 / `0x857559` |

Every applicable case has empty `ECVER` and `ECExec` fields. Each batch invokes
AFU with BIOS programming switches `/p /b /n`; D3 and C3 1.04 additionally
request reboot. C3 1.02 and DA550 also invoke Intel's ME flasher against a
separate ME image. None declares a standalone EC file or EC-flash operation.
The packaged generic `FwUpdateAPI.dll`/updater string inventories exposed no
additional firmware download endpoint; this is a bounded static search, not a
claim that no other vendor service exists.

UEFIExtract NE A76 flat `unpack` parsed and decompressed the BIOS trees.
The initial DA550 recursive `all` dump hit Windows path length limits; a fresh,
shorter filename and flat dump completed. The flat trees contained 3,606,
3,220, 3,220 and 3,191 binary files respectively for DA550, C3 1.04, C3 1.02
and D3, including duplicate parent/child representations. Deduplication by
SHA-256 left 1,821, 1,676, 1,676 and 1,667 unique binary objects.

ASCII and UTF-16 searches found no `ITE8390`, `Razer EC Firmware`, `ITE Tech.`,
`EC Firmware`, `ECFLASH`, `EC Flash`, `ECUpdate` or `Embedded Controller`
markers in those objects. These are discovery heuristics only: another ISA,
encoding or unlabeled payload would need independent identification.

The large raw-object inventory did not establish an EC image:

- GUID `2D27C618-7DCD-41F5-BB10-21166BE7E143` has the Intel authenticated-code
  module header: module type 2, header length `0xA1` dwords, vendor `0x8086`,
  and declared module length equal to its 183,104/187,136-byte body.
- GUID `365C62BA-05EF-4B2E-A7F7-92C1781AF4F9` contains Intel PXE/UNDI boot
  agent material on DA550 and legacy boot/USB storage content on C3/D3.
- Other large sections identify as DSDT, bitmap artwork or option ROMs.
  DA550's 131,072-byte `AMI_ROM_hole` was separately unpacked: its nested FFS
  volume contains Razer logos and boot/recovery artwork, not an identified EC
  image. Its SHA-256 is
  `80d788e104188d3ace9825923730c6616be192ece8c756c74be253f753c6d020`.
- `EcPs2Kbd`, GUID `2F72309E-D5B0-4A9D-84A9-1AB38C698F78`, is an x86-64
  PE image (machine `0x8664`), 3,872 bytes on DA550 or 5,376 on C3/D3.
  Its name does not make it controller firmware or establish a fan command.

Thus no EC image version, payload hash, ISA, complete fan-channel set,
zero-duty store, mailbox completion or verified Auto handback can be supplied
for these models from these packages. The BIOS hashes above must not be used
as substitute EC identities, and CH551's contract must not be inherited.

## RZ09-0351 and remaining acquisition limits

The [exact RZ09-03519 support page](https://mysupport.razer.com/app/answers/detail/a_id/3919/kw/instruction%20manual)
remains the model anchor. Razer support explicitly reported no BIOS updater in
[August 2022](https://insider.razer.com/razer-support-45/rz09-03519-bios-updater-38505)
and [September 2022](https://insider.razer.com/razer-support-45/rz09-03519-2020-razer-blade-15-bios-update-drivers-39720).
Those statements are historical availability evidence, not a permanent claim.
The legacy official download category `parentcategoryid=1099` returned HTTP 404
in this review. The current official firmware index, answer 4166, returned
Oracle's verification page on direct retrieval; indexed official records were
used to recover the other package leads. No usable exact 0351 BIOS or EC
carrier was obtained, so there is no artifact to hash or disassemble for it.

The next useful input for each family is an exact-model, attributable EC
payload from an official EC/combined/recovery or service package, or explicit
exact-model vendor source defining zero-duty and Auto recovery. For D3, C3
and DA550, repeating extraction of the pinned BIOS-only closures does not
resolve that gap. For 0351, package acquisition itself remains unresolved.
An unverified repair-site SPI dump or a related board's image would not supply
the missing provenance or justify runtime admission.
