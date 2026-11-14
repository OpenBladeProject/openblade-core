# Source-reviewed performance and fan coverage

The issue [#967](https://github.com/OpenBladeProject/openblade-core/issues/967)
review covers all 22 supported profiles. It adds exact-model manual fan controls,
Creator on RZ09-0328 and Maximum Power on RZ09-0530. Source review and physical
validation remain separate.

## Manual fan ranges

These bounds intersect each model's pinned Synapse middleware and UI definitions.
Every target write also requires fresh current-preset firmware metadata. All
ranges use 100-RPM steps. Fixed speed and host curves share the same typed targets
on the listed AC presets.

| Models | AC preset and RPM range |
| --- | --- |
| RZ09-0287, RZ09-0314 | Balanced 3700–5200 |
| RZ09-0301, RZ09-0330 | Balanced 3700–5300 |
| RZ09-0300 | Balanced 3500–5500; Gaming/Creator 3900–5500 |
| RZ09-0351 | Balanced/Gaming/Creator 3600–5200 |
| RZ09-0328 | Balanced 4300–5500; Performance/Creator 4900–5500 |
| RZ09-0370 | Balanced 3300–5400 |
| RZ09-0409 | Balanced 3400–5200 |
| RZ09-0427 | Balanced/Silent 3300–5400 |
| RZ09-0421 | Balanced/Silent 3400–5200 |
| RZ09-0423 | Balanced/Silent 2200–3800 |
| RZ09-0508 | Balanced/Turbo 2000–5400; Silent 2200–3800 |
| RZ09-0510 | Balanced 3400–5200; Silent 3400–3800; Turbo 3300–5400 |
| RZ09-0509 | Balanced/Silent 3700–5300; Turbo 3300–5400 |
| RZ09-0530, RZ09-0529 | Balanced 3400–5200; Silent 3400–3800; Maximum Power 3300–5400; Custom 4000–5300 |

RZ09-0529 also offers Turbo at 3300–5400 RPM. RZ09-0328 retains the existing
Performance name for its Gaming wire preset, preserving saved settings. Its
upstream-reviewed Silent and Custom performance controls retain their scope.

RZ09-0530 and RZ09-0529 additionally admit fixed targets in Battery Balanced,
using 3400–5200 RPM intersected with fresh metadata. Battery Saver remains
Automatic; battery curves remain unavailable. The other table entries require
confirmed sufficient external power for manual targets.

RZ09-0484 retains its own Balanced 2200–3800 and Silent 3700–5300 ranges; host
curves can use either admitted AC preset. RZ09-0482 and RZ09-0483 retain their
fixed-target backends and their existing battery fixed targets. RZ09-0484 also
retains Balanced fixed targets on battery. RZ09-0581 and RZ09-0528 retain their separately validated
controls and bounds. The [lighting review](source-reviewed-lighting.md) defines
lighting coverage for all 22 models.

## State, power and recovery

Service IPC 82 carries the expanded 5500-RPM value range and separates reviewed
profile envelopes from observed firmware limits. Temporary limits never rewrite
saved settings; curve samples are projected into fresh limits at submission.
Read-only protocol 51 clients receive bounded display placeholders. Existing standard and
legacy Maximum targets remain 5400 RPM. This requires a coordinated full update.

Range discovery reads the current preset directly. Paired performance and manual
flags must agree before and after metadata reads. Every target submission checks
the preset, power, controller and exact device, then independently reads both
targets. Cached per-preset metadata is presentation evidence, never write authority.

Curve entry and Fixed/Curve changes preserve the selected admitted preset.
Missing CPU temperature selects that preset's admitted maximum. Preset changes
from manual control still require explicit Automatic handback: the current range
does not establish a restorable destination range. Saved fan intent is retained.
Custom manual fans in this expansion are limited to RZ09-0530 and RZ09-0529.
Custom Maximum Fans remains a separate switch. Ambiguous submissions pause writes;
recovery observes state without replaying a request.

## Issue 967 and firmware findings

The RZ09-0287 archive records 30 failed adapter queries alongside successful
performance and lighting queries. A complete transfer failed firmware status
validation, but the archive omits the numeric status. New diagnostic exports
retain that correlated status while rejecting the failed reply and its payload.

The exact Synapse bundle enables the adapter query; its error fallback does not
establish available wattage. BIOS 1.04 exposes AC presence through AC0._PSR,
without an established normal-controller wattage bridge. Unavailable adapter
classification still blocks thermal changes. Physical recovery of the reported
laptop has not been established.

RZ09-0330 EC 1.01 also supplies no admitted typed version floor for CPU Boost.
Its separate F003 USB handler returns a constant for 00/81, rather than attesting
the normal keyboard controller's EC version.

## Source records

- [Older models](../evidence/source-reviewed-blades/2026-11-13-issue-967-older-models.json)
- [2024/2025 models](../evidence/source-reviewed-blades/2026-11-13-issue-967-modern-models.json)
- [RZ09-0328 and RZ09-0530](../evidence/source-reviewed-blades/2026-11-13-issue-967-middle-models.json)
- [All-model firmware ledger](../evidence/source-reviewed-blades/2026-11-13-issue-967-firmware.json)
- [Sanitized diagnostics and proof verification](../evidence/source-reviewed-blades/2026-11-13-issue-967-diagnostics.json)

The 118 source records and 311 UTF-8 excerpts were checked against downloaded
bytes. Records retain URLs, hashes and offsets, without vendor binaries or raw
user logs. The additions have software failure/recovery coverage; physical fan
response, restoration and lifecycle validation remain pending per model.
