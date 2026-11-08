---
title: admit reviewed disable-only Maximum Fans cleanup on battery
author: Jack
date: 2026-11-07
branch: feat/official-source-reviewed-support
touches:
  - CHANGELOG.md
  - docs/source-reviewed-blade-models.md
  - src/OpenBlade.Protocol/ExperimentalBladePort.cs
  - tests/OpenBlade.Protocol.Tests/SourceReviewedBladeModelTests.cs
  - evidence/source-reviewed-blades/2026-11-07-models.json
---

## Decided

On exact source-reviewed products 694/695/696 (RZ09-0508/0510/0509), the vendor non-Custom task runner clears Maximum Fans before its DC-Balanced branch. The shared helper reads the power-control byte and disables the selected flag without an AC prerequisite. The pinned source manifest is evidence/source-reviewed-blades/2026-11-07-models.json; reviewed 6737 transition/helper UTF-8 offsets are 694 726528 / 862317-862567, 695 1891619 / 2382594-2382842, and 696 1812996 / 2275780-2276028.

Admit only MaximumFans 07/0F [0] for that source family on confirmed battery power. Require agreement between Windows and a fresh firmware Battery adapter read. Keep the captured power source stable through native submission, exact host/controller/session/recovery guards, the whole-byte other-bit guard, independent cleanup readback before either mode-6 setter, and pause without replay after ambiguous or unconfirmed cleanup. Maximum Fans enable retains full-power AC admission. Legacy backends, product 711 and temporary-full-charge operations do not inherit this exception.

## Tried and ruled out

Changing only requireAdequateAdapter cannot fix the transition: the independent native ThermalSubmissionAllowed gate also rejects 07/0F on battery. Conversely, allowing every 07/0F command on battery would widen enable and unrelated flag authority. A model-scoped disable predicate shared by both gates, plus fresh adapter agreement, resolves the actual failure.

## Watch out

Three regression cases first enable Maximum Fans on AC, simulate battery power, then require confirmed cleanup before both DC preset writes. They failed before the fix. Additional cases cover contradictory adapter evidence, a power change at native submission, and unconfirmed cleanup pausing before preset writes without replay. These are software tests: unplugging does not have a physically established flag-persistence or RPM outcome. Track physical validation separately.
