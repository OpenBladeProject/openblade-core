# Blade 15 Advanced Mid 2021 and Blade Pro 17 2019

The later [lighting review](source-reviewed-lighting.md) adds bounded lighting controls to these profiles. Lighting exclusions below describe the original admission.

Issue [#949](https://github.com/OSSBlade/openblade-core/issues/949) requests
RZ09-0409. This review also adds both 2019 Blade Pro 17 variants, RZ09-0287 and
RZ09-0314. All three are officially supported on reviewed sources; physical validation is pending.
The [admission manifest](../evidence/source-reviewed-blades/2026-11-12-issue-949-models.json)
records complete source hashes, UTF-8 byte-offset excerpts and identity pins.

| Model | Exact system product | USB | Synapse | Custom levels |
| --- | --- | --- | --- | --- |
| RZ09-0409, Blade 15 Advanced (Mid 2021) | `Blade 15 Advanced Model (Mid 2021) - RZ09-0409` | `1532:0276` | 630 | CPU 0–3 including Boost; GPU 0–2 |
| RZ09-0287, Blade Pro 17 (2019) | `Blade Pro 17 (2019)` | `1532:0234` | 564 | CPU/GPU 0–2 |
| RZ09-0314, Blade Pro 17 (Late 2019) | `Blade Pro 17 (Fall 2019)` | `1532:024C` | 588 | CPU/GPU 0–2 |

## Evidence and command boundaries

The issue's identity report and Observer inventory establish the RZ09-0409
system name. The Observer export has no control operations: its native observer
was unavailable. It cannot prove a setting change or restoration. RZ09-0287's
exact system name comes from an immutable primary DMI report, paired with the
model-specific Razer guide and USB identity. SKU and BIOS are provenance.

Each model's pinned Synapse module exposes Balanced (raw 0) and Custom (raw 4).
The RZ09-0409 UI explicitly enables CPU Boost (raw 3); RZ09-0287 and RZ09-0314 use the ordinary
0–2 selector. All three use thermal query/write profile 1, CPU zone 1 and GPU zone 2,
interface 2, report ID 0, a 90-byte protocol body within a 91-byte native feature
report, and transaction IDs 1–31. The typed commands are `0D/82`, `0D/02`,
`0D/87` and `0D/07`; fresh `07/8C` reads establish adapter wattage.

Preset and level changes require full-power AC. Automatic fan handback retains
the confirmed current preset and levels, including battery Balanced raw 0, and
uses independent readback. It does not write manual targets. A manual baseline
requires explicit Automatic handback before performance changes because target
restoration is not yet admitted. Failed or ambiguous submission pauses writes;
recovery reads current state without replay.

Fixed fans, curves, lighting, charge limits, Maximum Fans, HyperBoost and native
GPU overclocking remain unavailable. Shared source methods do not establish
those features. The [companion catalog review](https://github.com/OpenBladeProject/blade-driver-catalog/pull/30)
checks every existing driver-catalog artifact with a separate
NotEstablished decision for each model; keyboard-driver and audio integration
are not admitted. Software tests cover failure and recovery, while physical
setting changes, restoration and lifecycle behavior remain untested.

## Late-2019 identity evidence

The [owner's primary system report](https://forum.manjaro.org/t/login-screen-not-showing-up-50-of-time-nvidia/83863)
pairs exact SMBIOS product `Blade Pro 17 (Fall 2019)` with USB `1532:024C` in the
same report. Product 588's own Razer guide identifies RZ09-0314, and its own
pinned middleware and UI establish the bounded controls. This closes the
identity gap recorded in the earlier 2019/2020 review.

The 2019 Synapse modules contain stale early/late support links. Those links
were not used as identity evidence, and neither model inherits the other's
commands or physical results. Each Windows registry identity source must still
match the exact product name before any runtime write.
