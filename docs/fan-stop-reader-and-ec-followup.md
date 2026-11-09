# Fan Stop reader and EC follow-up

Reviewed 2026-11-08 against `2aa6c4a5`. This follow-up covers the four
three-channel Blade 17/18 ECs and seven models that still need receiver-side
EC evidence. The retained vendor inputs were parsed as data. The results
below are from offline analysis and models of firmware ordering.

## Blade 17/18 read completion

The four pinned images expose a legacy read that uses different scratch
fields from [the legacy raw-address reader](fan-stop-bram3-readback-review.md).
Command `11` reads a byte from `80200 + index30`, writes it to result `32`,
then clears legacy command `12`. This arm leaves `13..15` alone. A zero
opcode branches directly to return and skips the clear. Index `E3` reaches
the same active-state byte as primary bank 0, index `06`.

| Model | Command 11 arm | Result store / command clear | Calls from scheduled event 6 to dispatcher |
| --- | --- | --- | --- |
| RZ09-0423 | `11A1A` | `11A2C` / `11DDA` | `1D02E → 1D01E → BA74` |
| RZ09-0484 | `12740` | `12752` / `12AFE` | `1D66E → 1D65E → C130` |
| RZ09-0509 | `12CDE` | `12CF0` / `130A6` | `1C1B6 → 1C1A6 → C62C` |
| RZ09-0529 | `1305C` | `1306E` / `13440` | `1C8F4 → 1C8E4 → C91C` |

Event 6 and the primary mailbox event 13 are dispatched from the same
firmware loop. If those callbacks execute serially, a fresh command `11`
reply to a request published after the D0 copy proves the D0 wrapper has
returned. The host does not have to publish a primary A0 fence while D0
still has its second clear pending. The candidate is one compound
observation with its complete sequence chosen before the first publication.

The offline zero-only model clears primary status, seeds its payload with
`FF`, and keeps the third-duty zero provisional after D0. It clears legacy
result `32` to zero before the fixed `11/E3` publication. Legacy command zero
and a reply with active bit 0 set complete the observation. This checks a
zero duty result. It would need a separate value contract to read arbitrary
duty values where `FF` might be a valid result.

| Model conditions | Explored states | Acceptances | Result |
| --- | ---: | ---: | --- |
| Serialized callbacks, intact staging, exclusive host | 171 | 8 | Each accepted read has both copies and D0 has returned. |
| Same, with an independent success-status writer | 242 | 8 | The payload sentinel still excludes acceptance without the D0 copy. |
| Independent status writer, stale zero payload allowed | 368 | 16 | False acceptance without D0 copy. |
| Cross-callback reentry allowed | 288 | 20 | Acceptance can precede the D0 return. |

The model permits extra primary notifications. In the first two rows a later
empty event 13 can still erase a subsequent Auto publication. A legacy
completion proves the end of the specific D0 wrapper under serialization.
The primary notification source still needs an applicable trigger contract
or an independent, preselected Auto route. An erased or ambiguous command
ends the attempt. The candidate never switches transports after a failed
publication.

The constant-address inventory found legacy reply and command stores in the
dispatcher. It also rejected apparent offset `230/232` matches that resolve
to `87230/87232` scratch and `80232` RAM. All 8,192 mapped descriptors were
checked for exact aliases to `F02212/30/32`. Startup clears and computed
stores remain outside that limited inventory. The staged fields and sentinels
need verified ownership across both windows, including exclusion of other
commands that can write the primary payload. The reader's time bound and
actual port behavior remain unmeasured. The `90` reader still has its
previously identified descriptor-sharing race.

## Independent EC I²C route

The asynchronous store review traced the IRQ formatting branch to three
fixed ROM formats. The longest writes at most 92 bytes including its NUL
into a 260-byte stack buffer. Decoding GP-relative instructions also resolved
nine previously unknown store addresses per image to ordinary RAM. These
results close those particular alias leads. The dynamic destination in the
I²C receive handler requires its own bound.

Each exact image initializes an eight-bank descriptor pointer table from
ROM. The I²C receive and transmit handlers use that table. In bank 0,
`1A` addresses selector `8035F` and `F0` addresses override `80425`, both
with metadata `2`. These accesses do not use primary command `93`. The
valid descriptors have no alias to third actual PWM `F01808`, primary
`93/94/A0`, or legacy `12/30/32`.

| Model | RAM descriptor pointer table | I²C RX data store / TX data load | Address `58` at setup call / store to `F01C48` |
| --- | --- | --- | --- |
| RZ09-0423 | `81120` | `2512` / `2494` | `15DC2` / `2457A` |
| RZ09-0484 | `81120` | `2512` / `2494` | `162E2` / `24BF2` |
| RZ09-0509 | `8113C` | `2AEA` / `2A6C` | `271D8` / `2373A` |
| RZ09-0529 | `81140` | `2B4E` / `2AD0` | `279C0` / `23E7A` |

Register `0F` maps to the I²C bank selector in every valid bank. The
receive path can write that byte without a local `<8` check. The alias
conclusion therefore depends on a valid bank value and intact pointer table.
Exact master-side request construction and exclusive ownership are material
to the proof. Initialization sets bank zero and address `58`, matching
write address byte `B0` on the keyboard MCU bus.

The exact keyboard images establish the other side of the endpoint. The
first three are in their respective [pinned EC carriers](fan-stop-blade18-source-findings.md#exact-inputs).
RZ09-0529 uses its separately signed keyboard updater, which targets
SSID `1A58300F` and PID `02C7`. Its carrier SHA-256 is
`30c06ba3d2d057593bbf3c812249d4edd020d17fc157a8cc7c29264a9e72f9dc`.
[Razer RZ09-0529 keyboard updater](https://mysupport.razer.com/app/answers/detail/a_id/19865)

| Model | Decoded MCU version and SHA-256 | Queued selector / commit / mode calls | Gated immediate read / write calls |
| --- | --- | --- | --- |
| RZ09-0423 | 1.02.00 `abe4b5db22faa2d73d7da390fc6b00844ab48d4e957519dcf62d666dd22bb350` | `278E` / `27A4` / `2660` | `B3E6` / `B408` |
| RZ09-0484 | 1.04.00 `e19171df923e0c786f9a0ee19f3080b3fc121842d7697d7fdc68cdd02b83fb5e` | `278E` / `27A4` / `2660` | `B682` / `B6A4` |
| RZ09-0509 | 1.01.00 `32bb690b242b524be8432e838e587e1ffd495df413eafb93341d4479d750fc1f` | `278E` / `27A4` / `2660` | `B8F4` / `B916` |
| RZ09-0529 | 1.07.00 `c0ebb3b40d3ac763a50d7301457d73f1a84a3e2c951730454d848b13ca3f3578` | `4FE2` / `4FF8` / `4F00` | `7860` / `7872` |

On each keyboard MCU, HID `0D/02` queues selector and mode. The worker
uses `B0:1A`, then `B0:0A` with value `05`, then `B0:36`. The
`0D/82` reply comes from the shared cache. SET can update it before the
bus write. A matching reply therefore cannot confirm Auto application.
Bank 0 maps `0A` to `80940`. The shared jump table at `902C` dispatches
value 5 to `9032`, where the handler zeros reply `80941` and sets bit 0
of `8033F`. Event 6 tests that bit before invoking the thermal worker.
The bus acknowledgement and this scheduling flag precede independent
confirmation of the applied thermal state.

The class `00` read/write arms use the same bus helpers synchronously and
propagate bus status. `00/22` sets a Boolean gate and `00/A2` reads it.
Startup sets the gate to zero. The immediate read uses a combined
address/read transfer and writes the reply bytes into the HID response.
It does not write EC register data. The gate admits both read and write
arms. The caller-controlled write can reach the I²C bank selector.

The known autonomous constant-address writes to `B0` avoid register
`0F`. One four-byte transfer begins at `90`. In bank zero, those logical
indexes address RAM `803C0..803C3`. None of the valid mapped descriptors
in any bank points into primary mailbox `F02290..F022A0`. The long
table-addressed LED writes use slave bytes `40/5E/4A`, with `54` also
present on KI8110. The fixed and RAM-indexed callers bound the selected
entries to those values. Indirect paths and forwarding callers still
limit the ownership conclusion.

This establishes an independent EC byte transport for these exact MCU
images. Its gate and bank ownership need a bounded contract.
Verified Auto also needs independent confirmation of the captured normal
selector, stage bytes and override permission. A guardian using this
transport must retain its handback ability if the caller exits.

## Seven models with missing receiver evidence

| Model | New evidence | Remaining EC evidence |
| --- | --- | --- |
| RZ09-0300 | Two exact DA530 selectors, SSID `1A582004`, in the signed Intel ME carriers. | D3 BIOS and the additional Intel ME packages have yielded no laptop EC implementation. |
| RZ09-0301 | Official keyboard firmware `1.00.01` for USB `1532:0245`, decoded and traced through the thermal I²C peer. | Receiver zero handling, fresh actual duty, tach provenance, and verified Auto. |
| RZ09-0328 | Two exact DA550 selectors, SSIDs `1A582011` and `1A58200C`, in the signed Intel ME carriers. | An exact EC implementation or equivalent vendor contract. |
| RZ09-0351 | Two signed OL510 selectors pin SSID `1A58200D`. They select Intel ME payloads. | An attributable, usable OL510 EC image. The exact-board dump mentioned in the repair post is restricted and comes from a board reported unable to start. |
| RZ09-0370 | Two older Razer-signed updaters contain the same P1106 BIOS image. Exact selectors include SSIDs `1A582013` and `1A582014`. | Both packages select BIOS only. The EC side of the BIOS mailbox remains unidentified. |
| RZ09-0482 | Microsoft-signed public catalog capsule for PI490 BIOS 1.06, with verified INF and ROM membership. | The older image retains the conditional OEM fan hook and the generic EC mailbox. The [keyboard MCU bridge](fan-stop-pi490-mcu-bridge-review.md) still ends at the receiver. |
| RZ09-0508 | Exact Synapse product 694 advertises firmware through the already reviewed BIOS 1.06 article. | PI4100's EC implementation and its fan contract. |

The RZ09-0301 keyboard carrier has SHA-256
`58f880cc46a64379131e7edc3d1a8c63418dc2989179d3a514d6d79ea38e49e5`.
The 3,673 Intel HEX records yield 58,892 bytes, SHA-256
`2b0073e46d07119027a44136f9cc9ca25d8ebd072418193e9a6f36c267720c98`.
The keyboard parser's class `0D` handler at `9C42` stages zone 1/2 targets
and forwards their raw bytes to wire address `B0`, registers `1B/1C`.
This includes zero. The mode worker writes the staged profile to `1A`,
commit value 5 to `0A`, then the staged mode to `36`. The current-speed
command `0D/88` at `9E44` reads a separate cache populated by a two-byte
I²C read of register `08` at `2960`. The target GET returns the same
cache that target SET populates. Current-speed GET itself does not initiate
a fresh transfer. [Razer keyboard updater](https://mysupport.razer.com/app/answers/detail/a_id/1967/)
· [Exact Synapse command definitions](https://apps.razer.com/synapse/products/581/mw/rzDevice25.f668146da6ee1d0b3b6c.js)

Exact product 624/669/694 Synapse resources expose ordinary fan mode and
speed paths. The shared duty method definitions do not establish a working
exact-model duty interface. In particular, the PI490 MCU's named duty
command is rejected. [Product 624](https://apps.razer.com/synapse/products/624/mw/installer-manifest.json)
· [Product 669](https://apps.razer.com/synapse/products/669/mw/installer-manifest.json)
· [Product 694](https://apps.razer.com/synapse/products/694/mw/manifest.json)

Package sources include the
[older PI410/PI411 updater](https://dl.razerzone.com/drivers/Razer%20Blade%2014%22%20%282021%29%20%7C%20RZ09-0370/RazerUpdater_v1.1.1.3_PI410_PI411-BIOSv0106v2.exe),
[PI490 catalog capsule](https://www.catalog.update.microsoft.com/ScopedViewInline.aspx?updateid=7dba8797-85b4-4d79-91b7-5f524c75ad21),
[RZ09-03519 downloads](https://mysupport.razer.com/app/answers/detail/a_id/5655/),
and [OL510 repair report](https://vlab.su/viewtopic.php?f=35&p=1129927&t=151751).

Detailed local proof and input manifests are under
`artifacts/model-expansion/bram3-followup/`,
`primary-reader-followup/`, `intel-missing-ec-followup/`, and
`amd-missing-ec-followup/`. The reader proof hashes all four EC images,
checks the opcode paths and calls, and reproduces the ordering model.
The package work verified signatures or catalog membership and inspected
the decoded contents offline. Runtime admission remains open for these
eleven models.
