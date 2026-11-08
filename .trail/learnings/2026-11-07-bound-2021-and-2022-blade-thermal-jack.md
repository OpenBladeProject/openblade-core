---
title: Bound 2021 and 2022 Blade thermal profiles to exact source selectors
author: Jack
date: 2026-11-07
branch: feat/official-source-reviewed-support
touches:
  - src/OpenBlade.Protocol/ExperimentalBladeModels.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.cs
  - evidence/source-reviewed-blades/2026-11-07-models-2021-2022.json
---

## Decided
Admit exact RZ09-0370/0270, RZ09-0427/028C, RZ09-0421/028A and RZ09-0423/028B identities through per-model source-reviewed thermal policy. All four use selector 1, CPU levels 0 through Boost 3, and GPU 0 through 2. The 2021 Blade 14 selects only Balanced and Custom, with battery Balanced raw 0; the 2022 models also select Silent and DC Balanced raw 6. None selects Turbo or direct Maximum Fans. Parameterize permissions rather than inheriting the later family presets.

## Tried and ruled out
Shared parser methods and generic fan UI do not establish model capability. Exact callers omit Maximum Fans; dynamic manual-fan discovery changes presets and does not establish safe target restoration. Firmware names and updater INI versions establish provenance, not a thermal command contract. Blade 15 customer updater BIOSVER=43.03 means Thunderbolt NVM metadata, not BIOS 2.06.

## Watch out
The three 2022 Windows product names contain the year although Linuxhw folder names omit it. Pin actual SMBIOS product text. Older 2022 firmware may lack the charge limiter: vendor articles document its addition, and every write requires a fresh valid limiter GET before SET. A failed charge query must not disable independent thermal controls or fabricate an inactive override. Source support and physical validation stay separate. Review every catalog artifact for each model and pin an immutable core catalog commit before refreshing offline coverage fixtures.
