# Synapse adapter wattage evidence

OpenBlade uses the official Razer Synapse bundles as the primary source for the
adapter wattage values returned by firmware command `07/8C`. This document
records the source bytes inspected on 2026-10-15. The bundles were downloaded
and read as data; no vendor code was executed.

The command is admitted only for the two physically supported models that use
these exact Synapse product modules:

| Laptop | Exact Windows product name | USB identity | Synapse product |
| --- | --- | --- | --- |
| Blade 16 RZ09-0528 | `Blade 16 - RZ09-0528` | `1532:02C6` | `710` |
| Blade 16 RZ09-0581 | `Blade 16 - RZ09-0581` | `1532:02E0` | `736` |

This evidence does not admit `07/8C` on another model. Experimental profiles
keep their existing power-source behavior and do not inherit this command or
these mappings.

## Command and reply

Both product modules name `07/8C` **Get Adapter Wattage Level**. The request has
the two-byte payload `0000`. A successful reply has two independent level
bytes:

- byte 0: current adapter wattage level;
- byte 1: recommended adapter wattage level.

Synapse reverse-maps each byte through the same table, parses the wattage from
the resulting label, and reports low power when the current wattage is nonzero
and lower than the recommended wattage. It does not compare the raw level
bytes. That distinction matters because level `0F` is 310 W while level `11`
is 280 W.

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

Every value not listed above is also unmapped. The table describes wattage,
not a connector. A 200 W reply is level `0C`, but whether it is below the
laptop's requirement depends on the recommended level in byte 1. The vendor
source does not establish that any wattage means USB-C, barrel, an original
adapter, or full power.

## OpenBlade admission

Synapse uses the comparison above for its low-power presentation. It treats a
zero current level specially: after a delay it retries while Windows says the
computer is on AC power and stops retrying on battery. Its persistent-zero AC
path can make seven queries with six exponential delays before returning false.
An unmapped value, `Unknown type`, or a query exception also produces false in
that policy. Therefore, Synapse returning “not low power” does not prove that
full power is available.

OpenBlade keeps the safer distinction between observation and admission:

- known level bytes decode to wattage, while `10`, `FF`, and every other
  unmapped byte remain unknown;
- `00` remains a zero-wattage observation and is not, by itself, a battery or
  connector identity;
- current and recommended wattage determine the power relationship only after
  both values decode;
- an unknown value, zero while Windows reports AC, or disagreement between
  the firmware observation and a fresh Windows power reading leaves the source
  unclassified and blocks behavior that requires confirmed full power;
- stable Windows battery evidence may classify the system as on battery, but
  it does not turn `00` into a vendor-defined connector value.

These rules deliberately fail closed where the Synapse UI policy returns false.
They preserve the existing exact-device, controller, session, power, and
readback checks before a hardware write.

When an official Synapse source is missing or cannot be read, the diagnostic
retention shipped in `0.27.5-nightly.89` provides the fallback. A diagnostic
export can retain the sanitized `07/8C` reply or the exact query failure after
an unrecognized reading or failed model check. That evidence remains diagnostic
until reviewed; it does not automatically add a mapping or admit a command on a
new model.

## Pinned vendor files

The SHA-256 values below cover the complete downloaded bytes.

| Product | Official URL | Bytes | SHA-256 |
| --- | --- | ---: | --- |
| 710 | [main.d7c1244bf00ff7462e7c.js](https://apps.razer.com/synapse/products/710/mw/main.d7c1244bf00ff7462e7c.js) | 190138 | `201541BA7E521BB4B161CFCD077BC95EA2577BFFC37B3452D1CCBFB57A57F626` |
| 710 | [rzDevice25.9e83fa81c0c26aa84ab0.js](https://apps.razer.com/synapse/products/710/mw/rzDevice25.9e83fa81c0c26aa84ab0.js) | 280091 | `DB01B1525A313EF333D71C4A5C3BCA1D6B657F78983761F1E06A0433284CFF15` |
| 710 | [6737.0a09be13ed46538a373d.js](https://apps.razer.com/synapse/products/710/mw/6737.0a09be13ed46538a373d.js) | 2379746 | `9D0E95894BE265652412EA84B1B52713A825D083BA0B65C6D9B91C70F659691C` |
| 736 | [main.b2e1b23c426e9bf93af6.js](https://apps.razer.com/synapse/products/736/mw/main.b2e1b23c426e9bf93af6.js) | 190572 | `A699B71BF0E825B733C8EB5E681F91E85538DBB596D68D8A26BA5F7E8CB3B043` |
| 736 | [rzDevice25.bcaf2d44cf6bcb983c6f.js](https://apps.razer.com/synapse/products/736/mw/rzDevice25.bcaf2d44cf6bcb983c6f.js) | 280091 | `BC7DC47E18261AB38393589D336C11F321CC431D700ED32147997144F64628CA` |
| 736 | [6737.45e4affd7675b37f3c36.js](https://apps.razer.com/synapse/products/736/mw/6737.45e4affd7675b37f3c36.js) | 2379746 | `CD29E8D89135EA967AB13B4607A6524087A6B1DDBF76F7C73A39C7E4C6EC404D` |

The full product-specific chunks have different hashes, but their adapter
command, decoder, level table, reverse lookup, and low-power policy segments are
byte-identical. Ranges below are zero-based byte offsets with an exclusive end.

| File | Evidence | Byte range | Segment SHA-256 |
| --- | --- | ---: | --- |
| both `main` files | reverse lookup used by the decoder | `[111413,111527)` | `E33A3653BE6F19E562ECA7C8C7C69E2086459ED7C2BB2C311208A8173BC2229C` |
| both `rzDevice25` files | reply decoder | `[51537,51867)` | `FEAFAC07FE57061304F9F00B346E0200DC998E42134359DFE09ADC50E531E012` |
| both `rzDevice25` files | `07/8C` command declaration | `[57627,57684)` | `2C2426EB318C31D3714EADEB33EE9D063838C9EFC84D9F806D5ED91449768F08` |
| both `rzDevice25` files | complete wattage-level table | `[59033,59251)` | `43A314D149436BACDB3A34088E20D9D450071859B49260FA5E014F137D8A8532` |
| both `6737` files | actual-versus-recommended policy | `[2313632,2314427)` | `F7BB4AA4CA406E0D20D262B17BBF9105784D5A2FC9FBF71761D63C304251F051` |

The product manifests provide the linkage to those chunks:

| Product | Evidence | Byte range | Segment SHA-256 |
| --- | --- | ---: | --- |
| 710 | `productId:710,vendorId:5426` | `[140426,140453)` | `ED5247CB4D31516522075C51A6790C669B44AE5DC5DF51524C2F85EB719BE115` |
| 710 | `rzDevice25LedMatrixBlade2` device type | `[164038,164092)` | `49F7886BAC30CF24DC5E9E5CE9FA599A0C89B3C04923586BA30294393EFDD0AD` |
| 710 | startup load including chunk 6737 | `[170321,170387)` | `BE45F3A27117450E249A0DFF9A443D501A28AE2F87D4312E9C2248AD089636BE` |
| 710 | logical module 1694 named `rzDevice25` | `[184137,184154)` | `5595F6061D8308194B6775CC0E5A074CDC8B375D0107B89590E64FBB8589C28A` |
| 710 | module 1694 content-hash reference | `[186173,186200)` | `83A48DCE5C37ACD2B70D481CC320C993300D1483156A42BFC5604A6E4D4C51B9` |
| 710 | chunk 6737 content-hash reference | `[187181,187208)` | `51263A8722AFDEA62E6B82B673469A3C67740127EC1F51A2793DE055688E2779` |
| 736 | `productId:736,vendorId:5426` | `[140458,140485)` | `992E506A8D9E00D786722A4681A2207D76EE2824711D416E746AD9E30C64BFE5` |
| 736 | `rzDevice25LedMatrixBlade2` device type | `[164223,164277)` | `49F7886BAC30CF24DC5E9E5CE9FA599A0C89B3C04923586BA30294393EFDD0AD` |
| 736 | startup load including chunk 6737 | `[170765,170831)` | `BE45F3A27117450E249A0DFF9A443D501A28AE2F87D4312E9C2248AD089636BE` |
| 736 | logical module 1694 named `rzDevice25` | `[184571,184588)` | `5595F6061D8308194B6775CC0E5A074CDC8B375D0107B89590E64FBB8589C28A` |
| 736 | module 1694 content-hash reference | `[186607,186634)` | `093CA3ADE14CA357AD299501F9A19E6C42D7EC5B2AE1D96356820567D32170D7` |
| 736 | chunk 6737 content-hash reference | `[187615,187642)` | `E6E08415E1BC572038E21F80F5300ED06B2404845F95E6BF6E9A93B456EDF76F` |

Only the small facts and hashes needed to audit the mapping are reproduced
here. The vendor bundles remain the cited source.
