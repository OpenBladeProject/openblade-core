# Synapse adapter wattage evidence

OpenBlade uses official Razer Synapse product bundles as the primary source for
the adapter wattage values returned by firmware command `07/8C`. This document
records the source bytes inspected on 2026-10-15. The bundles were downloaded
and read as data; no vendor code was executed.

## Admitted models

The command is admitted only for these seven exact registered models. Each row
has its own Synapse product module with the adapter-wattage feature enabled and
called. Finding the shared decoder in a bundle is not enough by itself.

| Laptop | Exact Windows product name | USB identity | Synapse product | Evidence status |
| --- | --- | --- | --- | --- |
| Blade 16 RZ09-0528 | `Blade 16 - RZ09-0528` | `1532:02C6` | `710` | Physically validated model; query is vendor-source-backed |
| Blade 16 RZ09-0581 | `Blade 16 - RZ09-0581` | `1532:02E0` | `736` | Physically validated model; query is vendor-source-backed |
| Blade 16 (2023), RZ09-0483 | `Blade 16 - RZ09-0483` | `1532:029F` | `671` | Experimental; source-backed, not physically validated in OpenBlade |
| Blade 14 (2023), RZ09-0482 | `Blade 14 - RZ09-0482` | `1532:029D` | `669` | Experimental; source-backed, not physically validated in OpenBlade |
| Blade 14 (2025), RZ09-0530 | `Blade 14 - RZ09-0530` | `1532:02C5` | `709` | Experimental; source-backed, not physically validated in OpenBlade |
| Blade 15 Base (Early 2020), RZ09-0328 | `Blade 15 Base Model (Early 2020) - RZ09-0328` | `1532:0255` | `597` | Experimental; source-backed, not physically validated in OpenBlade |
| Blade 18 (2023), RZ09-0484 | `Blade 18 - RZ09-0484` | `1532:02A0` | `672` | Experimental; source-backed, not physically validated in OpenBlade |

A future model must pin its own official product module, show that the feature
is enabled and called, and pass exact-device review. Missing evidence defaults
to no query and no thermal write admission.

## Command and reply

All seven product modules name `07/8C` **Get Adapter Wattage Level**. The request
has the two-byte payload `0000`. A successful reply has two independent level
bytes:

- byte 0: current adapter wattage level;
- byte 1: recommended adapter wattage level.

Synapse reverse-maps each byte through the same table, parses the wattage from
the label, and reports low power when the current wattage is nonzero and lower
than the recommended wattage. It does not compare the raw levels. That matters
because level `0F` is 310 W while level `11` is 280 W.

| Level | Synapse label | Watts |
| --- | --- | ---: |
| `00` | `0W` | 0 |
| `01` | `40W` | 40 |
| `02` | `45W` | 45 |
| `03` | `60W` | 60 |
| `04` | `65W` | 65 |
| `05` | `80W` | 80 |
| `06` | `90W` | 90 |
| `07` | `100W` | 100 |
| `08` | `120W` | 120 |
| `09` | `130W` | 130 |
| `0A` | `150W` | 150 |
| `0B` | `180W` | 180 |
| `0C` | `200W` | 200 |
| `0D` | `230W` | 230 |
| `0E` | `250W` | 250 |
| `0F` | `310W` | 310 |
| `10` | no mapping | — |
| `11` | `280W` | 280 |
| `12` | `330W` | 330 |
| `13` | `400W` | 400 |
| `FF` | `Unknown type` | — |

Every value not listed above is unmapped. The table describes wattage, not a
connector. A 200 W reply is level `0C`, but whether it is adequate depends on
the recommended level in byte 1. The vendor source does not establish that any
wattage means USB-C, barrel, an original adapter, or full power.

## OpenBlade admission

Synapse treats a zero current level specially: it retries while Windows says
the computer is on AC power and stops retrying on battery. Its persistent-zero
AC path eventually returns false. An unmapped value, `Unknown type`, a short
reply, or a query exception can also become “not low power” in the vendor UI
policy. That does not prove full power is available.

OpenBlade deliberately fails closed:

- both current and recommended levels must decode before their wattages are
  compared;
- `10`, `FF`, and every other unmapped value remain unknown;
- `00` is a zero-wattage observation, not a connector identity;
- zero while Windows reports AC, a query failure, a short reply, or disagreement
  with a fresh Windows power reading leaves the source unclassified;
- stable Windows battery evidence may classify the system as on battery, but it
  does not turn `00` into a vendor-defined connector value.

All seven profiles use the same `AdapterPowerCommandClient` and
`AdapterPowerProtocol` classification path. The five experimental profiles add
only per-model source admission; they do not copy or fork the decoder. They
perform a fresh `07/8C` query immediately before each non-battery thermal SET,
then recheck Windows power and identity at native submission. The former Windows-only `ExperimentalExternal`
fallback is not an admission path. Confirmed full power admits the profile's
normal thermal controls. Confirmed limited power admits only Balanced,
returning fans directly to Automatic while preserving the freshly confirmed
mode, and turning Maximum Fans off when required for that cleanup. Battery-mode
SETs retain their separate fresh battery checks.

Synapse explicitly clears Maximum Fans before leaving Custom on the applicable
models. OpenBlade reads the complete Power Mode Control byte, requires the other
bits to be clear, sends the whole-byte off value, and confirms the bit is off
before submitting `0D/02`. This is a narrow transition dependency. OpenBlade
does not broadly reset settings or treat `0D/02` as proof that Maximum Fans was
cleared.

Diagnostic exports can retain a sanitized `07/8C` reply or the exact query
failure. That evidence remains diagnostic until reviewed; it does not add a
mapping or admit a command on a new model.

## Pinned vendor files

The SHA-256 values cover the complete downloaded bytes.

| Product | Role | Official URL | Bytes | SHA-256 |
| --- | --- | --- | ---: | --- |
| 710 | main | [main.d7c1244bf00ff7462e7c.js](https://apps.razer.com/synapse/products/710/mw/main.d7c1244bf00ff7462e7c.js) | 190138 | `201541BA7E521BB4B161CFCD077BC95EA2577BFFC37B3452D1CCBFB57A57F626` |
| 710 | base device | [rzDevice25.9e83fa81c0c26aa84ab0.js](https://apps.razer.com/synapse/products/710/mw/rzDevice25.9e83fa81c0c26aa84ab0.js) | 280091 | `DB01B1525A313EF333D71C4A5C3BCA1D6B657F78983761F1E06A0433284CFF15` |
| 710 | policy | [6737.0a09be13ed46538a373d.js](https://apps.razer.com/synapse/products/710/mw/6737.0a09be13ed46538a373d.js) | 2379746 | `9D0E95894BE265652412EA84B1B52713A825D083BA0B65C6D9B91C70F659691C` |
| 736 | main | [main.b2e1b23c426e9bf93af6.js](https://apps.razer.com/synapse/products/736/mw/main.b2e1b23c426e9bf93af6.js) | 190572 | `A699B71BF0E825B733C8EB5E681F91E85538DBB596D68D8A26BA5F7E8CB3B043` |
| 736 | base device | [rzDevice25.bcaf2d44cf6bcb983c6f.js](https://apps.razer.com/synapse/products/736/mw/rzDevice25.bcaf2d44cf6bcb983c6f.js) | 280091 | `BC7DC47E18261AB38393589D336C11F321CC431D700ED32147997144F64628CA` |
| 736 | policy | [6737.45e4affd7675b37f3c36.js](https://apps.razer.com/synapse/products/736/mw/6737.45e4affd7675b37f3c36.js) | 2379746 | `CD29E8D89135EA967AB13B4607A6524087A6B1DDBF76F7C73A39C7E4C6EC404D` |
| 671 | main | [main.2ceb8f5607c0e7a5901a.js](https://apps.razer.com/synapse/products/671/mw/main.2ceb8f5607c0e7a5901a.js) | 167525 | `CACF8758BF8B4DF35B7D51536830149ED428960AFEDF08CC81367E75520551D1` |
| 671 | base device | [rzDevice25.5e330aa9fdf6f29eec62.js](https://apps.razer.com/synapse/products/671/mw/rzDevice25.5e330aa9fdf6f29eec62.js) | 270411 | `41E7758114AD3605B5B3EE0E65E79AF3055124F775904902BA3285F710AF55C9` |
| 671 | policy | [6737.e123a2b3f0516116025b.js](https://apps.razer.com/synapse/products/671/mw/6737.e123a2b3f0516116025b.js) | 1953339 | `54D3A9BBEC83568B02342AA54FF47A94EE55A48ECA85B5D9F28D60D01A0B1A9A` |
| 669 | main | [main.8e09720cb94da405eead.js](https://apps.razer.com/synapse/products/669/mw/main.8e09720cb94da405eead.js) | 172497 | `EEB8A171E77D1E066655D7FC997E4015B420B0F936F2464A27A87FDAFB5AC302` |
| 669 | base device | [rzDevice25.1094c437ecd0242743f8.js](https://apps.razer.com/synapse/products/669/mw/rzDevice25.1094c437ecd0242743f8.js) | 272267 | `D76F0E964537BCF19D517E67B16E9D8AC0160230FA779AFB3640DA78B6201AD3` |
| 669 | policy | [6737.439ff1a12463933a6346.js](https://apps.razer.com/synapse/products/669/mw/6737.439ff1a12463933a6346.js) | 1957041 | `D1AC2212AED5491D3EAB69ABAFA270B963A2336E859DF6BFBF9ED9A455E3791B` |
| 709 | main | [main.bb0dd1873a58a987c63a.js](https://apps.razer.com/synapse/products/709/mw/main.bb0dd1873a58a987c63a.js) | 187452 | `CC8E0EFD2B37F1ED5AF6C8CC8C3AB1327B9502927DC05F20EE7465486AB006F1` |
| 709 | base device | [rzDevice25.d532642d8de7a9cfcf5d.js](https://apps.razer.com/synapse/products/709/mw/rzDevice25.d532642d8de7a9cfcf5d.js) | 280091 | `9109826D790FEB8597BBFC115231066CA66E2C480ED9769BB85F3F91188AAD0A` |
| 709 | policy | [6737.d34a3f9a638030f35c6a.js](https://apps.razer.com/synapse/products/709/mw/6737.d34a3f9a638030f35c6a.js) | 2379746 | `4758FB7E2D8296564FB9CE971F6DCE3D580ED5407EEF07A69E1C362A04FF28C0` |
| 709 | selected backend | [rzDevice25LedMatrixBlade2.da34a4070bcb5ea45648.js](https://apps.razer.com/synapse/products/709/mw/rzDevice25LedMatrixBlade2.da34a4070bcb5ea45648.js) | 20253 | `41408CFD6CAB1117D49B13EB40FE68182EC395F63B2228C058BC0517A5C87E77` |
| 597 | main | [main.a1e24f33c1759c4315e3.js](https://apps.razer.com/synapse/products/597/mw/main.a1e24f33c1759c4315e3.js) | 169214 | `A864DC2BE2D35BC3703B81476C1659CAAA4D2C144C89616D9D5D0EE398F030FB` |
| 597 | base device | [rzDevice25.ae57bbfa911460c03510.js](https://apps.razer.com/synapse/products/597/mw/rzDevice25.ae57bbfa911460c03510.js) | 272265 | `2F41299C36E60E65D0BFCCE1E666FF67F0FC07F21F4523BED0A4E8CA5AC6043D` |
| 597 | policy | [6737.6da70458ec9e64b00f37.js](https://apps.razer.com/synapse/products/597/mw/6737.6da70458ec9e64b00f37.js) | 1957293 | `C6E6E76DFB61F960913E22B864A31273280547E21E1462DE3138F096D2DB56EA` |
| 597 | selected backend | [rzDevice25LedMatrixBladeWattage.5473a2df3a0c67e1323b.js](https://apps.razer.com/synapse/products/597/mw/rzDevice25LedMatrixBladeWattage.5473a2df3a0c67e1323b.js) | 2919 | `CF35C15059DA3456D357C811743D86FB1D1D6F28BDDE670601FAD30E8B329663` |
| 672 | main | [main.57245df0507455a5119e.js](https://apps.razer.com/synapse/products/672/mw/main.57245df0507455a5119e.js) | 168852 | `D523F5080784B07CB1888C39E636004626E073E2967B85999498030102F85FAE` |
| 672 | base device | [rzDevice25.85ce8530211c29f3c304.js](https://apps.razer.com/synapse/products/672/mw/rzDevice25.85ce8530211c29f3c304.js) | 272259 | `DFB3E3612D36B20910AF02E61A17ED64A1FFD3781E09C63BAAEE7CBEFDEBCA24` |
| 672 | policy | [6737.e50bb836b878190315d7.js](https://apps.razer.com/synapse/products/672/mw/6737.e50bb836b878190315d7.js) | 1957301 | `651F63DCC0EFC92B0FD7496EE70751707B5F650F20AF5C01C3255B61817C1997` |

## Activation audit

All seven base-device chunks contain the same complete wattage table and
`07/8C` command declaration. These exact source segments are shared even though
the complete bundle hashes differ:

| Evidence | Segment SHA-256 |
| --- | --- |
| `07/8C`, two-byte command declaration | `2C2426EB318C31D3714EADEB33EE9D063838C9EFC84D9F806D5ED91449768F08` |
| complete wattage-level table | `43A314D149436BACDB3A34088E20D9D450071859B49260FA5E014F137D8A8532` |

The five experimental manifests and policies add model-specific linkage:

| Product | Selected backend | Main feature flag | Startup call | Flag-gated query policy |
| --- | --- | ---: | ---: | ---: |
| 671 | `rzDevice25LedMatrixBlade` | `[50464,50615)` | `[146066,146094)` | `[156753,156893)` |
| 669 | `rzDevice25LedMatrixBlade2` | `[165451,165558)` | `[1214552,1214580)` | `[1224936,1225078)` |
| 709 | `rzDevice25LedMatrixBlade2` | byte `169091` | byte `1233065` | byte `1241232` |
| 597 | `rzDevice25LedMatrixBladeWattage` | byte `162317` | byte `1214756` | byte `1225184` |
| 672 | `rzDevice25LedMatrixBlade` | `[161753,161888)` | `[1214806,1214834)` | `[1225190,1225332)` |

Each selected backend inherits the common `rzDevice25` getter and does not
override it. Product 709 selects the `Blade2` backend even though its chunk
catalog also contains an optional Wattage-named backend. Catalog presence was
not treated as feature enablement.

For product 709, the main bundle selects `Blade2` at byte 162369 and loads the
policy chunk at byte 167723. The base command tuple is at byte 57644, its
zero-initialized two-byte request at byte 51537, the current and recommended
decodes at bytes 51681 and 51756, and the inherited getter at byte 142042. The
policy directly calls the getter at byte 2313856 and handles live
`POWER_WATTAGE_EVENT` data at byte 1133090.

For product 597, the main bundle selects `BladeWattage` at byte 160880 and loads
the policy chunk at byte 161682. The base command tuple is at byte 57640, its
zero-initialized two-byte request at byte 51533, the current and recommended
decodes at bytes 51677 and 51752, and the inherited getter at byte 140586. The
policy directly calls the getter at byte 1893128 and handles live
`POWER_WATTAGE_EVENT` data at byte 1112594.

Products 671, 669, and 672 use the same actual-versus-recommended wattage
comparison and the same vendor behavior for unknown values and query errors.
Their pinned policy files also explicitly send `07/0F [0]` to turn Maximum
Fans off before a non-Custom `0D/02` transition. Products 597 and 709 have the
same comparison and transition policy. OpenBlade adopts the source-backed
command and ordering while retaining its stricter unknown and failure admission.

The byte ranges above are zero-based with an exclusive end; single byte values
identify the start of the cited minified expression. Only the facts and hashes
needed to audit the mapping are reproduced here. The vendor bundles remain the
cited source.
