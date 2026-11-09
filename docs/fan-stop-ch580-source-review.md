# RZ09-0421 direct-duty source review

Reviewed 2026-11-08. Exact CH580 EC 1.03 establishes the existing two-channel
Stop and selector-first Automatic contract for RZ09-0421 (`1532:028A`).
Physical fan stop and live recovery remain untested. Runtime admission must
require the reported EC 1.03 compatibility version; it is not image attestation.

## Pinned inputs and transport

The [official update](https://mysupport.razer.com/app/answers/detail/a_id/9727)
provides the [EC/MCU carrier](https://dl.razerzone.com/drivers/Razer%20Blade%2015%22%20%282022%29%20%7C%20RZ09-0421x/RazerUpdater_v1.1.1.3_CH580_EC_0103_MCU_010300_v2.exe).
Both catalog hashes were verified before static analysis. The carrier signature
is Valid, Razer USA Ltd. No vendor code was executed or hardware accessed.

| Input | Bytes | SHA-256 |
|---|---:|---|
| Carrier | 1,516,336 | `6fcfd96615c514c6cd25944405980b6b39d8a2b0458f8cabce299dac37728118` |
| Block 6, `CH580_EC_01_03.bin` | 262,144 | `f96a009f65c49e89c76bb0874587472beace315a6cc616bc8da1bde845cb9c1b` |
| Block 13, managed EC flasher | 27,648 | `3de34bf3f5e57e0c11e76f45b773642ae3816f4a645040cf3b0602dcd30e79ed` |

Block 17 binds SKU RZ09-0421, SSID 1A58201B and EC 01.03. Block 10 binds
HID `1532:028A`; ToolConfig names CH580_EC_01_03.bin and SSID 201B.
The packaged InpOut x86/x64 libraries respectively hash to
`01bcec6ddb4964e1f5b69ba1bd3876221d8de7ae17cacfac66f095013434a78f` and
`5f27ed4d5cd58a1ee23deeb802e09e73f3a1d884ce2135f6e827f67b171269e7`.

PEReader inspection of the exact flasher proves WriteRzEcMbox token 06000049,
RVA 36C5 selects index port 300 then writes data port 301. ReadRzEcMbox token
0600004A, RVA 36E0 selects 300 then reads 301. I2C_Read_Write token 0600006B,
RVA 3D90 uses address 58, bank/index/count 90/91/92, payload A0, command 93
A0/A1 and status 94=80. Read publication IL0032..0064 and write publication
IL0221..0283 put command last, then poll command zero; IL03D4..03E5 checks status.

EC handler 203C independently bounds bank to 0..7 and count to 32 at
222E..223E. Status 80 is written at 224C before copying. Read loop 2272..2294
wraps index to a byte; write loop 22C4..22FC skips metadata 1 at 22E4, stores
at 22EC and marks change flag 8000E at 22F0. Caller 11580 calls the handler at
11582 and clears command at 1158C after return. Retain descriptor checks,
command-clear/status and a separate newly published readback.

## Register and fan contract

This NDS32 image uses base zero, little-endian data and big-endian instructions.
Reset 50A4..50CA copies ROM 2C544 to RAM 81000, length 15C. Pointer table
2C664 becomes RAM 81120; bank zero starts at 294D8. All 2,048 descriptors were
decoded. Each descriptor is LE32 metadata followed by LE32 backing address.

| Logical index | Metadata | Backing location | Meaning |
|---|---:|---|---|
|06|1|802E3|Active bit 0|
|08/09|1|80816/80817|Measured tach, 100 RPM/count|
|0A/0B|2/1|80940/80941|Separate policy command/status|
|1A|2|8035F|Selector|
|36|2|8034D|Manual flags|
|B0/B1|1|F01807/F01803|Actual PWM|
|B2|1|F01841|Shared live cycle|
|B8/B9|2|80394/80395|Staged direct duty|
|F0|2|80425|Override byte, bit 6 permission|

Policy 1483E checks active bit 0 at 14842..1484C. FE reaches 14C84 and checks
F0 bit 6; staged loads 14CB0/14CE0 and actual stores 14CCA/14CFA copy both
bytes unchanged, including zero. This FE path contains two PWM outputs.
Factory loader 1376C selects four groups at 3B000/3C000/3D000/3E000, each with
eight 512-byte records. All 32 headers encode two fan slots; 14394..14398
decodes that count. Generic extra slots do not enlarge this configured contract.

Measured accumulators 80510/80514 are divided by 100 and published at
1359C..135B4. Requested targets 80500/80502 are separate bookkeeping.
Initialization 131DE sets cycle B7, but admission must read the live nonzero
cycle and bound all staged/actual values against it.

## Automatic and limits

Normal selectors 0..7 leave FE. FE preserves last-normal selector 8034F;
reload at 14D3C depends on cache, refresh/manual state and 8044E. It calls
142EC, can clear manual flags at 14FC8 and updates the cache at 14FD0.
Supervisor 14258 skips feedback 13EFE only for FE at 142A0; otherwise it
calls feedback at 142A4 without requiring permission bit 6 clear first.
Restore the captured normal selector, independently observe normal selector,
manual zero and policy idle, then restore owned override and inactive staging.

Without permission FE sets selector zero at 14C94, which is not exact saved
mode restoration. F0 bit 6 also bypasses a policy contributor in 10974; bit 5
gates sensor updates. Preserve unrelated bits and reject drift. Policy 9008
returns at 9010 when command 0A is zero; policy idle is not mailbox drain.

The shared baseline and safeguards remain: active state, normal selector,
manual zero, policy idle, override bits 5/6 clear, valid duties and live cycle,
fresh power/temperature/controller/session/ownership checks, durable intent,
guardian recovery and ambiguity fencing. Requested zero, actual PWM zero,
tach zero and mechanical stopping are distinct. No host-loss cooling lease
or universal exclusion against other port clients is established.

The [offline profile](../tools/firmware/profiles/ch580-ec-1.03.json) independently
pins this image and all 13 fields. Static listings, exact host IL, input pins
and register map are retained under `artifacts/model-expansion/screen0421/`.
