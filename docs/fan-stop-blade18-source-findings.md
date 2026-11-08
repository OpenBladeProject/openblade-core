# Blade 17/18 Stop: three channels and a completion gap

Reviewed 2026-11-08. RZ09-0423, RZ09-0484, RZ09-0509 and RZ09-0529 each have an exact
source-confirmed FE direct-duty path for three PWM channels. Zero is copied
without a minimum clamp. Stop remains unavailable because the current
coordinator owns two channels and the candidate third-duty read has a distinct
completion race that has not been resolved. This is a concrete transport and
ownership gap; physical testing is not a substitute for resolving it.

## Exact inputs

All carriers were downloaded, checked against the source-reviewed catalog and
verified with valid Razer USA Ltd. signatures. Payloads were extracted as data.

| Model / EC | Official update | Carrier SHA-256 | EC SHA-256 |
|---|---|---|---|
| RZ09-0423 / DI780 1.05 | [Article 14775](https://mysupport.razer.com/app/answers/detail/a_id/14775) | `3f096851bed1679a83630fa588daf0d977f0944634075e75493658cfad03c554` | `ada7830bcca91f0201d108386a4c4efde39197bd31719f810db4481fd0e58dea` |
| RZ09-0484 / KI890 1.07 | [Article 9740](https://mysupport.razer.com/app/answers/detail/a_id/9740) | `6e383e9ea40ac5d45be0dbd38cba9c8f92368317d4ad06ad8da1d411e7edb2d7` | `a1d2e6ebe7de811661d0d61fd9e4ae108f8103fd8d65b1af72996faa1beadde2` |
| RZ09-0509 / KI8100 1.02 | [Article 14241](https://mysupport.razer.com/app/answers/detail/a_id/14241) | `6e06d131e1a77a733fbf5c37e5ba95ccda2e496257a7bb4747b1e274dda74c38` | `1d53c56d56581331c480bd12cb240b5657acc6ad5877711ffea2e4b8d7a073b1` |
| RZ09-0529 / KI8110 1.06 | [Article 20330](https://mysupport.razer.com/app/answers/detail/a_id/20330) | `922f1ae0b9c8bcac3563d9cb884c34e31128f5b51ac8fe75e48c0fcdac30933d` | `1cee6e614bfb086946f691cbc551863d6f697495159d23ede505df49fa47752d` |

Every EC image is 262,144 bytes, NDS32 with little-endian data and big-endian
instructions. Addresses below are file offsets. Host modules independently
establish index/data ports 300/301 and mapped 90..94/A0 transport. Full input
pins and instruction/IL evidence are retained in the ignored investigation
artifacts listed below.

## Established control behavior

All four models stage first/second duty at bank 0 B8/B9 and expose actual
F01807/F01803 at B0/B1. The third stage is bank0 index 9C→8038E; measured third
tach is 2C→80818, in 100 RPM/count. The third actual PWM is F01808, with the same
cycle F01841 exposed at B2. None of the 2,048 mapped descriptors in these images
exposes F01808.

| Model | FE stores: first / second / third | Normal feedback resumes | Mapped command clears after handler |
|---|---|---|---|
|0423|154B6 / 154E6 / 15514|14A5E permits 14510 unless FE|115E8|
|0484|159D2 /15A02 /15A30|14F7A permits14A2C unlessFE|1230C|
|0509|268F4 /26924 /26952|25E90 permits25942 unlessFE|128A0|
|0529|27048 /27078 /270A6|265F4 permits260A6 unlessFE|12BF0|

FE requires active-state bit 0 and override F0 bit 6. It takes over all three
channels and suppresses their ordinary feedback. Captured normal selectors 0..7
resume feedback before clearing permission; factory fallback is not exact
restoration. Normal table reload depends on last selector, refresh/manual state
and 8044E. A three-channel coordinator must capture, stage, verify and restore
all three duties, preserve unrelated override bits, and carry the third
channel through tach coast, journal, guardian and recovery.

## Candidate read and unresolved completion

The Blade 18 EC mailbox supports D0 at 208A..20CC; DI780 uses the equivalent
209A..20DC path: little-endian address bytes 9C..9F,
count 92 and result A0. A fixed read of F01808, count 1 would provide actual third
PWM without exposing arbitrary addresses or D1 writes. This mailbox address 9C
is distinct from bank0 logical index 9C.

D0 copies data, sets status 94=80 and clears command 93 at 2084 (2094 in DI780). It then returns
to the wrapper, which clears 93 again at the model-specific address above.
The first zero therefore proves that the copy completed, but provides no
observable guarantee that the wrapper has finished its trailing clear. A
following command could be cleared prematurely. This differs from the
existing mapped A0/A1 completion contract. Neither status 80 nor a guessed delay
establishes a drain; raw count errors can also return 80 without copying.

Admission needs a separately reviewed bounded read/completion contract and
complete three-channel ownership. The existing thermal, power, session,
controller and uncertainty gates remain applicable. No physical fan-stop or
live recovery result is claimed.

The DI780 carrier also includes the same managed flasher hash
`3de34bf3f5e57e0c11e76f45b773642ae3816f4a645040cf3b0602dcd30e79ed`
in block 25. Its bank table 29C08 independently maps third stage 9C and tach
2C, with no F01808 descriptor. Factory slot headers vary between two and four;
the consequential third FE/feedback path, not a guessed physical fan identity,
is the reason it cannot use the unchanged two-channel contract.

Detailed local evidence: `artifacts/model-expansion/screen0423/SCREEN.md`, `artifacts/model-expansion/intel0484/REPORT.md`,
`intel0509/review-report.md` and `intel0529/review-report.md` under the same
artifact root. The latter two include focused `reviewed-instructions.txt`,
`reviewed-host-il.txt` and exhaustive `register-map.json` files.
