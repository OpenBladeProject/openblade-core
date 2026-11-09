# RZ09-0427 direct-duty source review

Reviewed 2026-11-08. Exact PI480 EC 1.03 and 1.06 establish a two-configured-fan
Stop and selector-first Automatic contract for RZ09-0427 (`1532:028C`).
This is source evidence; physical fan stop and live recovery remain untested.
Only reported EC 1.03 and 1.06 are covered. The main address trace below is
for 1.03; the independently reviewed 1.06 changes are recorded in the addendum.

## Pinned inputs and exact host

The [official EC article](https://mysupport.razer.com/app/answers/detail/a_id/14993)
provides the [PI480 EC carrier](https://dl.razerzone.com/drivers/RazerUpdater_v1.1.1.3_PI480-ECv0103.exe).
Carrier and payload match the reviewed catalog; the carrier has a valid Razer
USA Ltd. signature. All analysis treated payloads as data. No vendor code,
WinRing0 library or driver was executed or installed; no hardware was accessed.

| Input | Bytes | SHA-256 |
|---|---:|---|
| Carrier | 599,792 | `0cfbb2516a93ac94a94a1ff220c81d210138cc37e44c3ef073effc684073ad2e` |
| Block 6, `PI480_EC_01_03.bin` | 262,144 | `312343a3cebd56fcb97cefcfc103b17bd481098e4122f2b6680ab46e93fc0b9f` |
| Block 7, managed EC flasher | 46,592 | `b914ce61b2a2623bb1ac6bb7ad6b5f547d349c0d249f11ab39c1c656daa06dec` |

Block 14 names RZ09-0427, SSID 1A58201C, EC 01.03 and
Razer_EC_Flash_v1.03.2.exe. Block 8 names the exact image and SSID 201C.

PEReader inspection of block 7 proves WriteRzEcMbox token 0600003D/RVA 464D
calls the port-byte delegate with index port 300, then value port 301.
ReadRzEcMbox token 0600003E/RVA 467C selects 300 then reads 301. The implementation
uses WinRing0 delegates; those vendor components are evidence, not a proposed
runtime dependency. I2C_Read_Write token 0600005F/RVA 4E9C binds address 58 to
bank/index/count 90/91/92, data A0, command 93=A0/A1 and status 94=80.
Read IL0032..0064 and write IL0221..0283 publish command last; command polling
and final status checks are preserved in the exact host IL artifact.

## EC map and completion

The image is RV32 with compressed instructions, mapped at 80000000. Subtract
that base for code file offsets below; table locations are given as file offsets.
Reset 8000013C..80000162 copies 38 bytes from file 20D9E to RAM 80081000.
Those first eight pointers reference bank descriptors; bank zero is file 1F850.
All 2,048 descriptors were decoded independently.

Handler 8000E9A6 checks bank <=7 and count <=32 at 8000EC4A..8000EC5C,
loads the bank pointer and sets status 80 at 8000EC72 before copying. Reads
use 8000EC96..8000ECC6; writes use 8000ECEA..8000ED34, skip metadata 1 at
8000ED18, store at 8000ED20 and set change flag at 8000ED24. Wrapper
80009598 calls the handler at 8000959C and clears command F02293 at
800095A4 after return. The mapped route therefore supports the existing
command-clear/status/descriptor check and separate fresh readback contract.
No generic D0/D1 route is needed or admitted here.

| Logical index | Metadata | Backing location | Meaning |
|---|---:|---|---|
|06|1|802E3|Active bit 0|
|08/09|1|80816/80817|Measured tach, 100 RPM/count|
|0A/0B|2/1|80940/80941|Separate policy command/status|
|1A|2|8035F|Selector|
|36|2|8034D|Manual flags|
|B0/B1|1|F01807/F01803|Actual PWM|
|B2|1|F01841|Shared cycle|
|B8/B9|2|80394/80395|Staged direct duty|
|F0|2|80425|Override byte, bit 6 permission|

## Configured fans, zero and Automatic

Policy 8001214E checks active bit 0 at 80012152..80012158. FE reaches
80012666, requires F0 bit 6 and copies B8/B9 directly into actual PWM at
80012690/800126C2, without a zero exception or minimum clamp. It also contains
generic third/fourth slots 8038E/8038F to F01808/F01809 at 800126F4/8001271C.
Those instructions do not establish four physical fans.

Exact factory loader 80010A4C selects groups 3B000/3C000/3D000/3E000 with eight
512-byte records each. All 32 headers encode two fan slots. Loader
80011A76..80011A7C decodes that count; the count-two path 80010E6A..80010E9C
sets the first two targets and explicitly clears target slots 3/4 at
80010E8C/80010E90. This establishes the configured two-fan contract, matching
the extra generic-slot distinction in the separate SO6110 review. No new
physical fan identity or control of unused slots is inferred.

Measured 80510/80514 accumulators are divided by 100 and published at
800107B2..800107CE. Requested targets 80500/80502 remain separate. Cycle
initialization 80010454 sets B7; runtime must read and validate the live cycle.

Normal selectors 0..7 leave FE. The normal branch at 800121D0 reloads policy
through 80011A0A when cache/refresh/manual state and 8044E require it. It can
clear manual flags at 80012238 and updates last-normal cache at 80012244;
FE preserves that cache. Supervisor 8001196E skips normal feedback 80011252
only for selector FE at 800119B8, and calls it at 800119BC otherwise. Permission
bit 6 does not gate this normal feedback call. Restore the captured normal
selector, observe normal selector/manual-zero/policy-idle, then restore owned
override and inactive staging bytes exactly. The permission-absent fallback
sets selector zero at 80012660 and is not exact restoration.

Policy command handler 800088D2 returns for zero; its separate status is not
host mailbox completion. F0 is a whole-byte register and bit 5 suppresses
sensor updates. Preserve unrelated bits and reject drift; no Intel-specific
bit-6 policy-contributor claim is transferred to this AMD image.

Retain the shared baseline, fresh power/temperature/controller/session/ownership
admission, nonzero cycle and duty bounds, guardian ownership, journal and
ambiguity fencing. Requested duty zero, actual zero, tach zero and mechanical
stop remain separate. No host-loss lease or universal port-client exclusion is
established. Reported EC version is compatibility evidence, not running-image
hash attestation.

The [offline profile](../tools/firmware/profiles/pi480-ec-1.03.json) pins the
independent RV32 image/map and 13 fields. Complete static evidence is retained
under `artifacts/model-expansion/screen0427/`.


## EC 1.06 addendum

The [official combined update](https://mysupport.razer.com/app/answers/detail/a_id/9724/)
provides BIOS 3.06, EC 1.06, MCU 1.01.00 and PD 11 for RZ09-0427. Its
[carrier](https://dl.razerzone.com/drivers/Razer%20Blade%2014%20%282022%29%20%E2%80%93%20RZ09-0427/RazerUpdater_v1.1.1.3_PI480-MCUv010100-PDv11-BIOSv3.06v1-ECv0106.exe)
and EC payload match the catalog pins; Authenticode verifies Razer USA Ltd.

| Input | Bytes | SHA-256 |
|---|---:|---|
| Combined carrier | 6,715,752 | `6dc6495a1bdebf7aea343f2a5f1e82e5565aece7d1fbf50cbd2e450cc18b04ef` |
| Block 12, `PI480_EC_01_06.bin` | 262,144 | `2482e6ded196b6378e92ad857cc07b2f9db0b9aa392996172d09548f6cf6ef86` |

Block 31 binds SKU RZ09-0427, SSID 1A58201C and EC 01.06; block 22 selects
the exact EC filename. The EC flasher in block 17 is byte-identical to the
1.03 carrier's block 7, including the independently inspected 300/301 host
mailbox protocol above. The larger BIOS block exceeds the existing bounded
NSIS decoder limit; it is not needed for this EC proof and was not executed.

RV32 startup 8000013C..80000162 now copies file 20EC6 to RAM 80081000;
bank zero is file 1F970. All 2,048 descriptors were compared. The sole change
is unrelated bank-zero C2: metadata/address `1/8008106C` becomes `2/808EE`.
All 13 fan, active, override and policy fields in the table above are unchanged.

| Contract | Exact EC 1.06 instruction addresses |
|---|---|
| Mapped mailbox | Handler 8000EAC2; bank/count bounds 8000ED66..8000ED78; status 80 at 8000ED8E; read 8000EDB2..8000EDE2; write 8000EE06..8000EE50; metadata-1 skip 8000EE34; store 8000EE3C |
| Completion | Wrapper 80009672 calls handler at 80009676, then clears F02293 at 8000967E |
| Active and FE | Dispatcher 8001226A; active check 8001226E..80012274; permission bit 6 at 80012782..8001278A; stages at 80012794/800127C6 copied unchanged to actual PWM at 800127AC/800127DE |
| Configured fan count | Factory loader 80010B68 selects the same four groups, all 32 headers still count 2; count decode 80011B92..80011B98; count-two target branch 80010F86..80010FB8 clears unused targets at 80010FA8/80010FAC |
| Cycle and tach | B7 cycle initialization 80010570; measured accumulators divided by 100 at 800108CE..800108EA |
| Normal Auto | Reload branch 800122EC calls loader 80011B26 at 800122F6; manual clear 80012354; normal cache update 80012360 |
| Feedback and policy idle | Supervisor 80011A8A excludes FE at 80011AD4 and calls normal feedback 8001136E at 80011AD8 independently of permission bit 6; policy command handler 800089A0 returns for zero at 800089A8/80008A3A |

The complete 452-instruction fan dispatcher and 30-instruction feedback
supervisor match 1.03 after verified code/table relocations. The generic
third/fourth FE stores remain at 80012810/80012838, while the configured
count-two loader clears the unused targets. Zero handling, shared fields,
Auto reload requirements and safeguards above therefore also apply to this
exact 1.06 image. This adds one reviewed firmware version for the same model;
it does not establish compatibility with other versions.

The [1.06 offline profile](../tools/firmware/profiles/pi480-ec-1.06.json) validates
all 2,048 descriptors and 13 annotations. Exact extraction, signature, complete
IL/disassembly, descriptor delta, factory headers and normalized instruction
comparison are retained under `artifacts/model-expansion/screen0427-0106/`.
