# Experimental HyperBoost on Blade 16 (2023)

Razer's [14 October 2026 Synapse release notes](https://insider.razer.com/release-versions-feedback-67/14-october-2026-synapse-chroma-for-windows-90679)
name Blade 16 (2023–2026) with the Razer Laptop Cooling Pad. This is model-family
availability evidence, not a command specification or evidence for padless use.
Blade 16 (2025), RZ09-0528 / `1532:02C6`, already has independently admitted
HyperBoost and padless HyperBoost in OpenBlade. Its command fixtures and physical
validation do not transfer to the 2023 laptop.

The experimental addition is scoped to the existing exact RZ09-0483 profile,
Windows product `Blade 16 - RZ09-0483`, USB `1532:029F`. Other experimental
Blade families retain their existing capabilities.

## Published compatibility and experimental scope

Razer's [current HyperBoost compatibility list](https://www.razer.com/technology/razer-hyperboost),
checked on 2026-10-16, names these models:

| Blade 16 | Model | Listed NVIDIA GPUs | OpenBlade status |
| --- | --- | --- | --- |
| 2023 | `RZ09-0483` | RTX 4060–4090 | Existing experimental profile; HyperBoost added here |
| 2024 | `RZ09-0510` | RTX 4060–4090 | Outside the current model registry |
| 2025 | `RZ09-0528` | RTX 5080–5090 | Existing validated backend with HyperBoost |
| 2026 | `RZ09-0581` | RTX 5070 Ti–5090 | Existing validated backend with HyperBoost |

Razer's [2024 support page](https://mysupport.razer.com/app/answers/detail/a_id/13849)
and [2025 BIOS page](https://mysupport.razer.com/app/answers/detail/a_id/15235)
confirm the distinct model numbers. RZ09-0528 is the 2025 laptop.

The other experimental profiles—Blade 14 (2023 and 2025), Blade 18 (2023),
and Blade 15 Base (2020)—are absent from that list. They gain no HyperBoost
write capability. Observing a generic mode-7 value is not a model-specific
HyperBoost support claim. The 2024 Blade would require a separate exact-model
backend; it is outside this change's existing-experimental-model scope.

### Conflicting Blade 14 source evidence

Product 669's exact Blade 14 (2023) module includes a HyperBoost performance-mode
entry, despite the model being absent from the published compatibility list.
OpenBlade records that difference as observed-only capability and diagnostic
provenance; no HyperBoost write path is admitted for it.

- [669 main](https://apps.razer.com/synapse/products/669/mw/main.8e09720cb94da405eead.js),
  SHA-256 `EEB8A171E77D1E066655D7FC997E4015B420B0F936F2464A27A87FDAFB5AC302`:
  exact product identity near character 142,139 and mode entry near 145,359.
- [669 policy](https://apps.razer.com/synapse/products/669/mw/6737.439ff1a12463933a6346.js),
  SHA-256 `D1AC2212AED5491D3EAB69ABAFA270B963A2336E859DF6BFBF9ED9A455E3791B`:
  pad/firmware selection policy near character 1,468,407.

Blade 18 product 672 has no HyperBoost entry in its exact mode list. A shared
enum reference is insufficient to enable it. Its existing observed-only state
remains unchanged.

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
It clears Maximum Fans before leaving Custom. OpenBlade uses its existing
`07/8F` whole-byte guard before any `07/0F` cleanup, blocking that write when it
would clear local dimming or another active option. It never substitutes a
Balanced, Performance, or Automatic entry step. Custom CPU/GPU levels and fixed
fan targets must be independently readable before they can form saved intent.

Every submitted entry command requires fresh exact identity, controller,
full-power adapter and the journaled pad path, including at native submission.
Pad-present entry requires the exact pad; padless entry requires confirmed
absence. Ambiguous discovery admits neither path.
The exact product module also declares laptop MCU firmware 1.5.0.0 as its
minimum. Entry queries `00/87` (four zero request bytes, four version bytes)
and requires that minimum; this is an explicit source-backed MCU restriction,
not a BIOS-version restriction. Exit remains available without this entry gate.
Battery cleanup uses this model's DC Balanced raw mode 6; AC Balanced is raw 0.

Pad loss between selectors stops the sequence. A possibly submitted or
unconfirmed write pauses all experimental writes; no alternate sequence or
setter replay follows. The existing service ownership journal records saved
performance, Custom levels and fan intent for explicit exit and lifecycle
cleanup. Exiting remains available after pad removal. Low-power cleanup selects
the admitted Balanced/Automatic destination directly and preserves saved intent.

This is source-backed experimental support. No physical HyperBoost, thermal,
sleep/resume or crash-recovery validation on an RZ09-0483 was performed for this
change. Existing RZ09-0528/0581 physical results do not establish those outcomes.
