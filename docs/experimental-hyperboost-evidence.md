# Experimental HyperBoost on Blade 14 (2023) and Blade 16 (2023, 2024)

Razer's [14 October 2026 Synapse release notes](https://insider.razer.com/release-versions-feedback-67/14-october-2026-synapse-chroma-for-windows-90679)
name Blade 16 (2023–2026) with the Razer Laptop Cooling Pad. This is model-family
availability evidence, not a command specification or evidence for padless use.
Blade 16 (2025), RZ09-0528 / `1532:02C6`, already has independently admitted
HyperBoost and padless HyperBoost in OpenBlade. Its command fixtures and physical
validation do not transfer to the 2023 or 2024 laptops.

The experimental additions use the existing exact RZ09-0483 (`1532:029F`),
RZ09-0482 (`1532:029D`) and RZ09-0510 (`1532:02B7`) profiles and their own
Synapse definitions.
Other experimental Blade families retain their existing capabilities.

## Published compatibility and experimental scope

Razer's [current HyperBoost compatibility list](https://www.razer.com/technology/razer-hyperboost),
checked on 2026-10-16, names these models:

| Blade 16 | Model | Listed NVIDIA GPUs | OpenBlade status |
| --- | --- | --- | --- |
| 2023 | `RZ09-0483` | RTX 4060–4090 | Existing experimental profile; HyperBoost added here |
| 2024 | `RZ09-0510` | RTX 4060–4090 | Existing source-reviewed profile; HyperBoost added from product 695 |
| 2025 | `RZ09-0528` | RTX 5080–5090 | Existing validated backend with HyperBoost |
| 2026 | `RZ09-0581` | RTX 5070 Ti–5090 | Existing validated backend with HyperBoost |

Razer's [2024 support page](https://mysupport.razer.com/app/answers/detail/a_id/13849)
and [2025 BIOS page](https://mysupport.razer.com/app/answers/detail/a_id/15235)
confirm the distinct model numbers. RZ09-0528 is the 2025 laptop.

The 2024 Blade uses its own product-695 definitions, described below. The Blade
14 (2025), Blade 18 (2023), and Blade 15 Base (2020) gain no HyperBoost writes. A shared mode-7
enum alone does not establish an exact model's command contract.

### Blade 14 (2023): exact Synapse definitions

Product 669's exact Blade 14 module declares `isHyperBoostSystem`, a HyperBoost
mode entry, its cooling-pad policy, and MCU minimum 1.1.1.3. This source admits
experimental support despite the model's absence from the public list. Its
ordinary modes are Balanced (0), Silent (5), and Custom (4); Performance (2)
is not offered or substituted as an entry step. Both pad paths use its own
paired thermal setter and independent readback. Padless use remains a
source-backed extrapolation awaiting community hardware testing.

| Official product-669 source | SHA-256 |
| --- | --- |
| [main.8e09720cb94da405eead.js](https://apps.razer.com/synapse/products/669/mw/main.8e09720cb94da405eead.js) | `EEB8A171E77D1E066655D7FC997E4015B420B0F936F2464A27A87FDAFB5AC302` |
| [6737.439ff1a12463933a6346.js](https://apps.razer.com/synapse/products/669/mw/6737.439ff1a12463933a6346.js) | `D1AC2212AED5491D3EAB69ABAFA270B963A2336E859DF6BFBF9ED9A455E3791B` |
| [rzDevice25.1094c437ecd0242743f8.js](https://apps.razer.com/synapse/products/669/mw/rzDevice25.1094c437ecd0242743f8.js) | `D76F0E964537BCF19D517E67B16E9D8AC0160230FA779AFB3640DA78B6201AD3` |
| [rzDevice25LedMatrixBlade2.344be717d50b54c297de.js](https://apps.razer.com/synapse/products/669/mw/rzDevice25LedMatrixBlade2.344be717d50b54c297de.js) | `79EFDEC6D4C38F30ED067F9F4E8FC1E5F731D463040C75D627F405684E4562CC` |

Useful character offsets: main 142,139 (identity), 142,487 (MCU minimum),
145,359 (HyperBoost mode), and 164,419 (`isHyperBoostSystem`); policy 1,468,407
(pad/firmware gate) and 1,876,601 (DC Balanced 6); base codec 59,644 and 70,580
(thermal GET/SET); subclass 8,491 (overclocking dispatch, no mode-7 extra write).

The source clears Maximum Fans before non-Custom transitions. Product 669 also
offers Maximum Fans as a user-facing Custom switch with the Blade 16 setter
contract ([source review](../evidence/source-reviewed-blades/2026-11-19-rz09-0482-maximum-fans.json)),
so the Blade 14 now represents it in Custom intent. HyperBoost entry and ordinary
transitions out of Custom use the same guarded `07/0F [0]` cleanup as the Blade
16: a fresh `07/8F` must show no other option set, and the cleared bit is
confirmed before the mode setter. On exit, the service compares the journaled
Custom intent with the confirmed flag and writes Maximum Fans back on. A refusal
after the confirmed cleanup and before mode 7 reports a known state.
Blade 18 product 672 has no HyperBoost entry in its exact mode list. A shared
enum reference is insufficient to enable it. Its existing observed-only state
remains unchanged.

### Blade 16 (2024): exact Synapse definitions

Product 695's exact Blade 16 module declares `isHyperBoostSystem`, a HyperBoost
mode entry with its own fan range, the shared mode-7 value and MCU minimum
1.3.0.0. Its ordinary modes are Balanced (0), Silent (5), Turbo (1) and Custom
(4), with DC Balanced (6) on battery. It has no Performance (2) mode, so Turbo,
the default AC mode, is the pad-present entry base. Silent, Balanced, Turbo and
Custom enter directly and are restored on exit on AC. The battery exit hands off to
DC Balanced raw 6 and confirms it as DC Balanced. Both pad paths use the same paired setter and
independent readback as the 2023 laptop; padless use remains a source-backed
extrapolation. No accessory-status write emulates a pad.

| Official product-695 source | SHA-256 |
| --- | --- |
| [main.27df2e7ddab0b6bc34cc.js](https://apps.razer.com/synapse/products/695/mw/main.27df2e7ddab0b6bc34cc.js) | `A12CD7047CC1CF093DBB458D703BA4D18CFE77110F47391B180EA5B33BA5E379` |
| [6737.7ec91e479ef65c194b6c.js](https://apps.razer.com/synapse/products/695/mw/6737.7ec91e479ef65c194b6c.js) | `22D359EB7EDA08A9D2F4766EB6898A875A5F009EDB666BA4CD5A0425491AD1DA` |
| [rzDevice25.60bfcef58690c8bb715d.js](https://apps.razer.com/synapse/products/695/mw/rzDevice25.60bfcef58690c8bb715d.js) | `2D69449422DAC50C23A0EC81A5421CAB178BD04610725273FA67497574195491` |
| [ui main.65ed4ee5.js](https://apps.razer.com/synapse/products/695/ui/static/js/main.65ed4ee5.js) | `74ECA71816B57B4C9BE3F83D3E5D894F95A2050D88FB2DF4B63A5AFFF5271612` |

Useful byte offsets: main 48,781 and 48,922 (raw mode enum, including mode 7),
117,552 (Turbo sent as raw 1), 141,152 (identity), 141,503 (MCU minimum),
144,362 and 145,857 (Turbo and HyperBoost mode entries), 158,285
(`defaultACPerformanceMode:1`), 163,099 (`needHyperboostRecheck`), 163,410
(`isHyperBoostSystem`) and 101,870 (`systemSKU` getter); policy 1,255,621 (SKU
predicates), 1,256,337 and 1,278,858 (`shouldDisableHyperboost`), 1,793,192 and 1,795,779 (mode and pad
offer gated on it) and 2,386,571 (`isFirmwareSupported`). The
[evidence record](../evidence/source-reviewed-blades/2026-11-20-rz09-0510-hyperboost.json)
lists each finding.

Product 695 omits `hasMaxFanSpeedModeEnabled` and `hasCpuBoostEnabled`. In its policy the
first gates only the Maximum Fans clear and reapply around mode changes (bytes
1,892,259 and 1,893,528) and a startup UI restore (1,295,124); the second gates a
separate battery CPU Boost toggle (1,295,285). The UI still renders the Maximum
Fans switch (ui byte 6,906,405) and the CPU Boost level (6,906,371), and the
Maximum Fans setter handler (policy byte 1,189,750) is ungated, so both existing
admissions stay. Synapse therefore does not clear Maximum Fans before HyperBoost
on this laptop. OpenBlade deliberately deviates: it keeps the guarded cleanup it
uses on the 2023 laptops and restores the saved Custom intent on exit, so every
mode-7 laptop shares one cleanup path.

### System SKU recheck

Products 671 and 695 set `needHyperboostRecheck`. Their policies then set
`shouldDisableHyperboost` when the tenth character of the Windows system SKU
(`systemSKU.charAt(9)`) is `R`, `S`, `5` or `6`, and Synapse then offers no
HyperBoost mode and no pad offer. OpenBlade matches that on RZ09-0483 and
RZ09-0510: it declines new HyperBoost entries on those configurations and reports
why. The check never limits turning HyperBoost off, restoring the saved mode, or
recovering an interrupted session, so a laptop that is already in HyperBoost can
always leave it. Like Synapse, a SKU that is missing or shorter than ten
characters admits HyperBoost: `charAt(9)` then returns an empty string, which
matches no excluded character. The SKU is read again whenever the laptop is
opened, so a later read can add or remove the restriction. Other controls and
host admission are unaffected, and diagnostics record only the evaluated
character. Product 669's policy has a `5`/`6` predicate behind the same feature
guard, but its main module never requests the recheck, so the RZ09-0482 stays
ungated.

The meaning of the character is not established. The RZ09-0510 updater lists four
SKU cases, each with its own graphics BIOS: `RZ09-0510S` with 95.06.2F.00.1E,
`RZ09-0510T` with 95.04.5F.00.39, and `RZ09-05102` and `RZ09-05103` with
95.03.3B.00.2F ([models record](../evidence/source-reviewed-blades/2026-11-07-models.json)).
So the character changes with the graphics BIOS family, but no reviewed source
names the GPU or explains why Synapse excludes the `S` case. OpenBlade follows
the predicate as written.

### Battery exit

On battery, every HyperBoost exit hands off to Balanced (DC raw 6), whatever base
the session owns, and the readback confirms Balanced or DC Balanced with
Automatic fans. The owned base and the saved profile stay as intent, and the next
AC reconciliation restores them. Synapse sends Balanced on battery as raw 6
(policy byte 1,398,122 for 671, 1,558,223 for 669 and 1,892,480 for 695) and
restores its pre-HyperBoost mode only on AC (bytes 172,278, 1,240,504 and
1,295,406). Before this change the port refused a battery exit from any base
other than Balanced, so a session entered from Performance, Silent, Custom or
Turbo could stay in HyperBoost after unplugging. The shared fix applies to RZ09-0483,
RZ09-0482 and RZ09-0510.

### Manual fans

Synapse's setter writes `[1, selector, mode, 0]` from any state (695 policy
setter at byte 1,891,618; fan flag `auto:0` at byte 2,362,420), so it can leave
mode 7 with manual fans set by its own UI. OpenBlade reports that state as
HyperBoost with manual fans, without reading fan targets, and admits the exit
from it; the readback must confirm Automatic fans. Entry still requires a
confirmed ordinary mode.

## Vendor evidence

The official Synapse product 671 module enables `isHyperBoostSystem`, supplies
HyperBoost fan parameters, includes HyperBoost profile settings, and maps
`Hyperboost_Mode` to firmware mode 7. The product-specific policy exposes the
mode only when a cooling-pad device reports `fanReady` and supported firmware.
OpenBlade enables both paths experimentally using that exact mode-7 sequence.
Padless access is recorded as source-backed extrapolation, rather than vendor
padless support or physical validation. No accessory-status write is used to
emulate a pad. The command implementation and pinned source hashes are below.

| Official product-671 source | SHA-256 |
| --- | --- |
| [main.2ceb8f5607c0e7a5901a.js](https://apps.razer.com/synapse/products/671/mw/main.2ceb8f5607c0e7a5901a.js) | `CACF8758BF8B4DF35B7D51536830149ED428960AFEDF08CC81367E75520551D1` |
| [6737.e123a2b3f0516116025b.js](https://apps.razer.com/synapse/products/671/mw/6737.e123a2b3f0516116025b.js) | `54D3A9BBEC83568B02342AA54FF47A94EE55A48ECA85B5D9F28D60D01A0B1A9A` |
| [rzDevice25.5e330aa9fdf6f29eec62.js](https://apps.razer.com/synapse/products/671/mw/rzDevice25.5e330aa9fdf6f29eec62.js) | `41E7758114AD3605B5B3EE0E65E79AF3055124F775904902BA3285F710AF55C9` |

Reviewed as source data on 2026-10-16; vendor code was not executed. These are
content-named bundles, with hashes pinned above for reproducibility. All are
single-line minified sources; useful character offsets are approximately:

- main: 24,800 (product identity), 28,495 (supported HyperBoost mode), 65,951
  (`isHyperBoostSystem`), 150,564 (mode 7).
- policy: 881,200 (ready-pad availability), 972,464 (profile/mode mapping),
  1,397,600 (mode setter and Maximum Fans cleanup).
- device: 2,500 (thermal payload codec), 268,551 (GET/SET command declarations).

## Transition contract

The four-byte thermal setter is `0D/02 [1, selector, mode, fanFlag]` for selectors
1 and 2. HyperBoost uses mode 7 and Automatic fan flag 0. OpenBlade retains the
existing model-specific `0D/82 [0, selector, 0, 0]` getter and requires both
returned selectors to agree on mode and fan flag. Neither an acknowledgement
nor one selector alone confirms the requested state.

Synapse directly selects the requested mode from the previous ordinary mode.
On the Blade 16 it clears Maximum Fans before leaving Custom. OpenBlade uses its existing
`07/8F` whole-byte guard before any `07/0F` cleanup, blocking that write when it
would clear local dimming or another active option. It never substitutes a
Balanced, Performance, or Automatic entry step. Custom CPU/GPU levels and fixed
fan targets must be independently readable before they can form saved intent.

Every submitted entry command requires fresh exact identity, controller,
full-power adapter and the journaled pad path, including at native submission.
Pad-present entry requires the exact pad; padless entry requires confirmed
absence. Ambiguous discovery admits neither path.
The exact product modules declare MCU minimum 1.1.1.3 for Blade 14,
1.5.0.0 for Blade 16 (2023) and 1.3.0.0 for Blade 16 (2024). Entry queries `00/87` (four zero request bytes, four version bytes)
and requires the model-specific minimum; this is an explicit source-backed MCU restriction,
not a BIOS-version restriction. Exit remains available without this entry gate.
All three models use DC Balanced raw mode 6; AC Balanced is raw 0. The 2023
laptops report raw 6 as Balanced; the 2024 laptop reports it as DC Balanced, and
the exit readback accepts that preset only on battery.

Pad loss between selectors stops the sequence. A possibly submitted or
unconfirmed write pauses all experimental writes; no alternate sequence or
setter replay follows. The existing service ownership journal records saved
performance, Custom levels and fan intent for explicit exit and lifecycle
cleanup. Exiting remains available after pad removal. Low-power cleanup selects
the admitted Balanced/Automatic destination directly and preserves saved intent.
A battery exit hands off to Balanced from every base, as described above.

This is source-backed experimental support. No physical HyperBoost, thermal,
sleep/resume or crash-recovery validation on an RZ09-0482, RZ09-0483 or RZ09-0510 was
performed for these changes. Existing RZ09-0528/0581 physical results do not establish those outcomes.

The explicit Blade 14 cooling-pad entry baseline is carried in IPC protocol 67.
Protocol-66 readers reject the added nested member, so service and clients must
be updated together through a full installation. IPC protocol 86 adds the Turbo
base slot for the 2024 laptop and the HyperBoost entry restriction, and admits
HyperBoost with manual fans, under the same full-installation rule; protocol 85
is reserved by a separate pending change.
