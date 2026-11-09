# RZ09-0510 direct-duty source review

Reviewed 2026-11-08 against core commit `71193867c99dcabde57fd1a56322eae75d072f05`.
The exact SO6100 EC 1.02 and packaged host code establish the existing two-channel
Stop and selector-first Automatic contract for RZ09-0510 (`1532:02B7`). This is
source-reviewed evidence. Physical fan stop, live restoration and fault recovery
remain untested on this model. No runtime admission is made by this document.

## Pinned inputs

The [official update article](https://mysupport.razer.com/app/answers/detail/a_id/14884)
links the [EC/MCU/vBIOS carrier](https://dl.razerzone.com/drivers/Razer%20Blade%2016%22%20%282024%29%20%7C%20RZ09-0510/RazerUpdater_v1.1.1.3_SO6100_EC_0102_MCU_010300_VBIOS.exe).
The download matches the reviewed catalog and has a valid Razer USA Ltd.
Authenticode signature. All payloads were treated as data; no updater, vendor
assembly or driver was executed, and no hardware was accessed.

Adapter classification uses the existing exact Product 695 `07/8C` source
contract in [source-reviewed Blade models](source-reviewed-blade-models.md).
Its pinned command module is `rzDevice25.60bfcef58690c8bb715d.js`, SHA-256
`2D69449422DAC50C23A0EC81A5421CAB178BD04610725273FA67497574195491`.
The guardian's adapter factory admits this getter only and contains no setters.

| Input | Bytes | SHA-256 |
|---|---:|---|
| Carrier | 6,946,120 | `2912408c872d57176e42d2da09b68566cecf9b723f51cce4ae4e717a9e98e295` |
| NSIS block18: `SO6100_EC_01_02.bin` | 262,144 | `d2ba85d82e76b594c448eaaa578df0ec2817af5af509d548a54a51a3845e4d43` |
| Block3: managed updater | 382,216 | `a20887bdb544bc70e12d1d2e429aadbd24f51d99e33ecc1110c563379d6217c7` |
| Block22: x86 InpOut library | 98,304 | `01bcec6ddb4964e1f5b69ba1bd3876221d8de7ae17cacfac66f095013434a78f` |
| Block23: x64 InpOut library | 98,304 | `5f27ed4d5cd58a1ee23deeb802e09e73f3a1d884ce2135f6e827f67b171269e7` |

Block20 ToolConfig names the EC image and SSID300A. Blocks11/12 explicitly
select `1532 02B7`. The catalog records model-specific SKU cases. An unrelated
block24 failed the strict LZMA framing check and was not used as evidence;
this does not change the independently hash-matched EC/host inputs above.

## Host mailbox and descriptors

PEReader inspection of block3 proves `RWIo.WriteRzEcMbox` token0600001F/RVA2167
selects the index at port300 then writes at301. `ReadRzEcMbox`
token06000020/RVA217F selects at300 then reads301. `I2C_Read_Write`
token06000051/RVA2E90 accepts address58, prepares bank/index/count90/91/92,
dataA0, and publishes command93 last (A0read/A1write). Read IL0014..0043
and write IL017E..01CE are followed by polling93==0; IL02D2..02E6 checks94==80.
These are static metadata/IL facts; the updater polling loop is not reused.

EC handler203C independently establishes this mapping. At2272..2290 it bounds
bank0..7/count0..32 and sets status80 before the copy. The read loop is
22B2..22D4. The write loop2324 skips metadata1;232C stores a permitted byte
and2330 sets change flag8000E. Caller11EB4 invokes203C at11EB6 and clears
F02293 at11EC0 only after return. Status alone, a payload buffer or a skipped
write is insufficient; command-clear, descriptor check and separately published
fresh readback remain required.

Reset50A4..50CA copies ROM2CA48 to RAM81000, length15C. ROM2CB68 supplies the
bank pointers at RAM81120. Bank0 is ROM29A24; descriptors are little-endian
four-byte metadata followed by four-byte address.

| Index | Metadata | Backing location | Meaning |
|---|---:|---|---|
|06|1|802E3|Active bit0|
|08/09|1|80816/80817|Measured tach,100RPM/count|
|0A/0B|2/1|80940/80941|Separate policy command/status|
|1A|2|8035F|Thermal selector|
|36|2|8034D|Manual flags|
|B0/B1|1|F01807/F01803|Actual PWM duty|
|B2|1|F01841|Shared PWM cycle|
|B8/B9|2|80394/80395|Staged direct duty|
|F0|2|80425|Override byte|

## Stop, channels and Automatic

Policy14EBE checks active bit0 at14EC2..14ECC. FE dispatch14EE6..14EEA goes
to1530A, which requires F0bit6. Loads15340/15370 and stores1535A/1538A copy
B8/B9 unchanged into actual PWM, without a zero exception, conversion or minimum
clamp. Requested-RPM bookkeeping at80500/80502 is separate. Measured values
80510/80514 are divided by100 and published at13C02..13C16. Initialization13806
sets cycleB7, but requests must use the freshly read nonzero cycle.

This FE branch contains exactly two output channels. Factory loader13DCE selects
3B000/3C000/3D000/3E000; each group has eight512-byte records. All32 headers
have low three bits2, and14A14..14A18 decodes this field into fan-slot count80547.
The generic initialization/tach code contains extra slots; their presence does
not add a third channel to this exact model's reviewed FE policy.

Normal selectors0..7 dispatch at14EF6 onward. FE returns without overwriting
last-normal cache8034F. Normal reload153DC depends on cache mismatch, refresh,
manual state and8044E;1568C may clear manual flags and156AC updates the cache.
Supervisor148D8 calls feedback1457E at14924 unless selector isFE (14920).
F0bit6 does not gate this normal feedback call. Thus restore the captured normal
selector first, independently observe normal selector/manual-zero/policy-idle,
restore owned override bits, then restore inactive staging exactly. No forced
Balanced intermediate or permission-first reset is established.

Without F0bit6, FE falls back to factory selectors at15314..15324; that is not
exact restoration. F0bit6 also bypasses a policy contributor in1124C; bit5 gates
sensor updates elsewhere. Preserve unrelated bits and reject drift. Policy
handler9008 returns when0A is zero at9010; idle admission is not a drain token.

The baseline contract remains: active state, normal selector, manual flags zero,
policy idle, override bits5/6 clear, valid staging/actual counts and nonzero
cycle. Stage both zero bytes, set only owned F0bit6, selectFE last, and use fresh
independent actual/tach observations. Requested zero, actual zero, tach zero
and mechanical stop remain distinct claims.

No firmware host-loss cooling lease or universal port-client exclusion is
established. Retain guardian ownership, independent temperature sampling,
lifecycle handling, durable journal, process handoff, bounded exposure,
per-write admission and ambiguity fencing. This source evidence does not
replace those safeguards or claim physical validation.

The [offline profile](../tools/firmware/profiles/so6100-ec-1.02.json) pins and
annotates this image independently. Ignored static evidence, host IL, full EC
listings, register map and factory headers are under
`artifacts/model-expansion/intel0510/`.
