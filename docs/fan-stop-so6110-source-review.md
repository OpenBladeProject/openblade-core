# RZ09-0528 direct-duty source review

Reviewed 2026-11-08 against core commit
`71193867c99dcabde57fd1a56322eae75d072f05`.
The exact SO6110 EC 1.09, MCU 1.09 and packaged host code establish the
register, transport and Automatic handback contract needed to share the
existing guarded host-mailbox primitive with RZ09-0528 (`1532:02C6`).
This is **source-reviewed evidence; physical fan stop and recovery are untested**.
Other AMD models are not admitted by this review.

The [offline profile](../tools/firmware/profiles/so6110-ec-1.09.json) pins the
image and carrier, decodes all 2,048 bank descriptors, and annotates the 13
fields used by the existing transaction/coordinator. It supplies no live
control API. Thermal tables are omitted: the 0528 manual-extension layout
differs from the existing SO6120 decoder, as documented in the
[earlier BIOS/EC review](bios-ec-source-validation.md).

No updater or vendor assembly was executed, ACPI evaluated, device accessed,
software or driver installed, or hardware setting changed. Vendor payloads and
full derived listings remain ignored under `artifacts/model-expansion/amd`.

## Pinned inputs

The official [EC article](https://mysupport.razer.com/app/answers/detail/a_id/21226)
and [MCU article](https://mysupport.razer.com/app/answers/detail/a_id/19765)
bind the downloads to RZ09-0528. Both fresh carriers have valid Razer USA Ltd.
Authenticode signatures. The fresh EC download also matches the carrier
previously pinned by the BIOS/EC review.

| Input | Bytes | SHA-256 |
|---|---:|---|
| [SO6110-ECv0109.exe](https://dl.razerzone.com/drivers/RazerBlade16RZ09-0528/SO6110-ECv0109.exe) | 9,110,504 | `3bf4dbec9555de4f784e9602b2fe982ec439924b946c412f46db4c53a769832b` |
| EC payload `SO6110_EC_0109.bin` | 262,144 | `44dc4e4c92ea4be9def93670a95b2bf859cb648b8dfe393fb5877735debbef5d` |
| Packaged `Razer Updater.exe` | 390,856 | `876e3939dec12a6135c4f30f525af36ff021fdc8a10389c5d71d1d234354f6a9` |
| [SO6110-MCUv01.09.00.exe](https://dl.razerzone.com/drivers/RazerBlade16RZ09-0528/SO6110-MCUv01.09.00.exe) | 1,442,040 | `4d62ce4ae2c0faaa48f69e8a18816140ca23f1c9cb1cbc0d18a07a716f4025bc` |
| MCU Intel HEX payload | 166,654 | `234388a76438541b34907e46b95026ac7e91893640d369d98e69b9c7aaead7d9` |
| Decoded MCU image | 59,420 | `371c590b86e578720a063bd2e1ee6795a233dfb54dc09613472e4cfbe4d648c1` |
| BIOS 2.02 DSDT | 40,255 | `aa91c2da04dd656016958e785ed6f1e2f41ecd222e5d7cca1e906fcc775e6521` |

The EC payload is NSIS member 15, stored-length offset `0x32657F`; the updater
module is member 3 at `0x2E061`. Fresh static extraction matches the retained
Ghidra EC image. The EC uses RV32 compressed instructions, mapped at
`0x80000000`; subtract that base from EC code addresses below for file offsets.
Startup copies the bank pointers from file `0x23D16` into `0x80081000`.

The MCU payload is member 10 at `0x10756F`, named
`SO6110_keyboard_FW_v1.09.00_20250918.hex`. All HEX framing, lengths and checksums
passed; 59,232 populated bytes occupy the 59,420-byte image, with gaps filled
`FF`. Its packaged command names `1532 02C6`; configuration names RZ09-0528 and
MCU `01.09.00`. MCU addresses below are file offsets. Ghidra analysis used the
image's own boot/application vectors, read-only flash and volatile peripheral
regions. Consequential branches and jump tables were checked against bytes and
instructions rather than relying on incomplete decompiler output.

## Exact host transport and completion

PEReader metadata/IL inspection of the packaged updater establishes:

- `RWIo.WriteRzEcMbox`, token `0600001F`, RVA `2167`, writes port `300` with
  the index, then port `301` with the value. `ReadRzEcMbox`, token `06000020`,
  RVA `217F`, writes the index to `300`, then reads `301`.
- `RWIoI2C.I2C_Read_Write`, token `06000051`, RVA `2E90`, uses the mapped
  address-`58` branch: bank `90`, index `91`, count `92`, data at `A0` onward,
  and command `93=A0` for reads or `93=A1` for writes, published last.
- One-byte read preparation/publication is IL `0014..0043`, followed by
  polling `93==0` at `0048..0052`. Write preparation/publication is
  `017E..01CE`, followed by polling at `01D3..01DD`. Final IL `02D2..02E6`
  reads status `94` and accepts `80`.

In the EC, handler `0x80014F1C` consumes MMIO `F02290..F02294` and data
`F022A0`. Instructions `0x800151C8..0x800151F0` bound bank to `0..7` and count
to `0..32`, select the bank pointer and set status `80` **before** copying.
Read loop `0x80015210..0x80015240` and write loop
`0x80015264..0x800152AE` wrap the index to eight bits. The write loop skips
metadata `1`, stores a permitted byte at `0x8001529A`, and marks a change flag.
Thus status `80` can accompany a skipped read-only field.

Caller `0x8000ECAC` invokes that handler at `0x8000ECB0`, then clears command
`F02293` at `0x8000ECB8` after it returns. Bank, length and command errors are
`84`, `82` and `83`. The existing transaction's command-zero, status,
descriptor checks and separate newly published readback match this exact
contract. Status alone, the staged payload, or an MCU cache does not confirm
the requested field changed. Mailbox payload offset `A0` is distinct from
bank-zero register `A0`, which happens to be a read-only unrelated field.

BIOS 2.02 DSDT independently defines a `NoLock` SystemIO region at `300..303`
and the `BMRD/BMWD` index/data helpers. Its separate `RECR/RECW` ACPI EC methods
attempt the private `ECMT` mutex but do not check the acquisition result.
Neither that mutex nor the updater's application-instance mutex provides
universal exclusion. No physical contention is claimed. These are the same
kind of residual ownership limitations already handled by the
[shared guarded workflow](so6120-host-mailbox-contract.md), not a reason to
replace it with the updater's unchecked polling loop.

## Exact register and control contract

Bank-zero descriptors are at file `0x232A8 + index*8`: little-endian four-byte
metadata followed by a four-byte pointer. Metadata `1` is read-only in the
mapped handler; reviewed writable controls have metadata `2`.

| Index | Backing location | Established meaning |
|---|---|---|
| `06` | `84EE3` | Active-state bit 0 gates policy and feedback |
| `08/09` | `80816/80817` | Coarse measured-speed publication, 100 RPM/count |
| `0A/0B` | `80940/80941` | Separate policy command/status |
| `1A` | `8035F` | Normal selector `0..7`, internal `FE/FF` |
| `36` | `8034D` | Manual flags; bit 0 selects ordinary manual handling |
| `B0/B1` | `F01807/F01803` | Actual PWM register bytes |
| `B2` | `F01841` | Shared PWM cycle byte |
| `B8/B9` | `80394/80395` | Staged raw direct-duty bytes |
| `F0` | `80425` | Override flags, including direct-duty permission bit 6 |

Policy `0x8000915E` requires active bit 0. Its dispatch at
`0x8000918A..0x8000919C` separates `FE/FF` before the normal range. The `FE`
branch at `0x800096F6` requires `F0.6`; stores at `0x80009720/0x80009752` copy
`B8/B9` directly into the PWM registers, without a zero exception or percent
conversion. When permission is absent, `0x800097B2` changes the selector to
zero. That fallback is not exact restoration of an arbitrary saved profile.

The branch also updates requested-RPM-derived target words and bookkeeping.
Additional firmware slots `8038E/8038F` feed `F01808/F01809`; this review does
not assign physical fan identities to those slots. Exact factory records
specify two fans. No new control of those additional slots is proposed.

Normal control writes zero at `0x80008292/0x800082FE`; lifecycle code clears
the same PWM registers at `0x8000740C/0x80007410`. Initialization at
`0x8000720C..0x80007214` sets cycle `B7` (183), but runtime must read the live
cycle and bound requests against it. Raw counts, requested zero, actual PWM
zero, tachometer zero and mechanical stop are separate claims.

The existing coordinator's baseline requirements are supported: captured normal
selector, manual flags zero, idle policy command, override bits 5 and 6 clear,
active state, nonzero cycle, and requested/actual duties within that cycle.
Staging `B8/B9`, setting only permission bit 6, and selecting `FE` last preserves
that contract. Whole-byte override writes must preserve other bits. Exact
0528 consumers include input/state behavior for bit 1, charger policy for
bits 3/4, other PWM paths for bit 0, and sensor-update suppression for bit 5.
The 0581-specific claim about a bit-6 power contributor is not transferred here.

## Automatic handback

Restoring the captured normal selector directly leaves the `FE` branch.
Supervisor `0x80008946` skips ordinary feedback `0x8000822A` only when the
selector is `FE`, at `0x8000898C..0x8000899C`; permission bit 6 does not gate
that normal feedback branch. Therefore the shared ordering applies: restore
the normal selector, independently observe it with manual flags zero, restore
the owned override byte, then restore inactive staging bytes exactly. No
intermediate Balanced selection or permission-first reset is established.

`FE` retains last-normal selector `8034F` and clears stage bookkeeping.
Reload depends on previous selector, refresh/manual state and `8044E`;
`0x80009254` can clear manual flags during a reload. Fresh final selector,
manual-zero and policy-idle observations remain necessary. The conclusion is
observed normal control and owned-byte restoration, not settled fan speed or
a cooling guarantee.

Policy command 5 has a separate path: the pointer at file `0x1DBE0` leads to
`0x8000DE10`, which clears policy status, sets `8033F.0`, then clears the
policy command at `0x8000DE2C`. Scheduled work at `0x8000FAD6..0x8000FAF2`
invokes the evaluator and records status 4. That status alone does not prove
Automatic or PWM response, and pending policy work is separate from host
mailbox completion.

The exact MCU confirms why the host route is needed. Thermal handler `9248`
rejects SET `0D/0D` through `9286 -> 92C2 -> 941C`, with status 5. GET `0D/8D`
also resolves to `941C` through raw switch table `92A3`. The generic Synapse
[product-710 base module](https://apps.razer.com/synapse/products/710/mw/rzDevice25.9e83fa81c0c26aa84ab0.js)
contains duty wrappers, but their presence does not establish model support.
Its SHA-256 is `db01b1525a313ef333d71c4a5c3bca1d6b657f78983761f1e06a0433284cff15`.

Normal SET `0D/02` bounds selectors to `0..7` at `9318/931A`, caches mode and
flags in `100001CC/CD`, and queues work at `100000C8`. Worker `4AF4` writes
EC index `1A` at `4CEE`, then `0A=5`, and later writes manual flags `36` at
`4C10`. GET `0D/82` returns the MCU cache. A pending mode read at `4CC6` can
overwrite that cache before a queued write; neither a successful HID response
nor matching cached GET is independent EC handback proof.

Generic gate `00/22` at `71B8` enables raw SMBus `00/21` and `00/A1` routes,
but does not suspend normal worker activity. The proven host-mailbox route
needs neither that gate nor a shared SMBus bank-selector change. Do not expose
those raw commands or use them as retries after an ambiguous operation.

## Integration boundary

All 13 logical fields, host publication/completion rules, direct entry and
selector-first Automatic ordering match the existing primitive independently.
The active-state backing address differs from 0581, but the admitted logical
index and bit do not. Share the typed protocol and coordinator; supply distinct
0528 host/HID/firmware evidence and update guardian/journal identity admission
together.

| Area | Delta from the existing SO6120 primitive |
|---|---|
| Host transaction | No demonstrated wire change: ports `300/301`, descriptor `90/91/92`, command `93`, status `94`, payload `A0`; command clears after the handler returns. |
| Direct-duty coordinator | Same admitted logical fields and entry/selector-first handback ordering; use the exact 0528 evidence rather than a model alias. |
| Identity and recovery journal | Add exact RZ09-0528 / `1532:02C6` and reviewed EC 1.09 admission together in the host, guardian and recovery paths. |
| Offline decoding | Separate RV32 image/hash/base and descriptor table; no reuse of SO6120 thermal-extension decoding. |
| Runtime observations | Retain fresh temperature and ownership gates; this review adds no EC temperature identity or physical validation. |

Retain fresh ownership, power, controller, lifecycle, telemetry and readback
gates, bounded exposure, durable intent, ambiguity fencing and caller-loss
recovery. An unavailable or stale temperature source must retain the existing
exit behavior. The literal-backed 0528 ACPI thermal zone is not a CPU sensor;
this review admits no replacement EC temperature source. Tach publication at
`0x80007580..0x8000759C` divides measured accumulators by 100. Normal manual
requests use 100 RPM/count, while Automatic table targets use 50 RPM/count;
neither is an actual PWM duty percentage.

Source-reviewed support can use this contract without claiming physical
validation. Actual fan behavior, live restoration and recovery under hardware
faults remain untested on 0528. No other AMD model shares this admission until
its own exact firmware and transport contract are reviewed.
