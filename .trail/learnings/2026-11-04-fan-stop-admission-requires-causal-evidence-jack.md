---
title: Fan-stop admission requires causal evidence; zero often means Automatic
author: Jack
date: 2026-11-04
branch: feat/battery-saver-low-fan-validation
touches:
  - CHANGELOG.md
  - Directory.Build.props
  - README.md
  - README.ru-RU.md
  - README.zh-CN.md
  - build/CiImpactManifest.json
  - build/LowFanValidationInstalledState.ps1
  - docs/battery-saver-low-fan-validation.md
  - docs/fan-power-source-policy.md
  - docs/silent-low-fan-validation.md
---

## Decided
Withhold production fan-stop admission on every Windows factory path while retaining ordinary direct manual control. The Battery Saver 2000-to-1500 record observed measured zero, but the 2000 preparation lasted only about 5.4 seconds and had no sustained control or return-to-2000 phase. It establishes association, not a reliable forced-stop capability. Keep original hardware records immutable and add a separate offline reassessment. No further hardware test is authorized by a user's uncertainty or by online research.

## Tried and ruled out
Plugged-in Silent/manual retained 1500 and separately authorized literal-zero targets while actual fans remained near 1900/2000 RPM; calibration did not establish lower physical steady speed. Online primary-code review found cooling-pad Off mislabeled as a laptop lead, zero mapped to Automatic in other tools, and older-model low-target clamps. None supplies an exact 02E0 fan-off command. Saved EC version already matches Razer's 1.04 release. Preserve natural Automatic idle stopping as a separate observed behavior.

## Watch out
A UI zero value, stored zero target, command acknowledgement, and measured zero rotation are different evidence. The historical spunDown flag is an observed outcome, not causal proof. The dormant prototype must not be admitted from ordinary direct-transition evidence. Register every new PowerShell build-contract suite with explicit CI impact inputs: the low-fan installed-state helper originally failed hosted CI because its new offline suite lacked that rule. The shared restoration helper may restore only a confirmed missing original UntilPowerSourceChange override after an unambiguous probe restoration; stop rather than replay an uncertain write. Trail check's 400 KB cap and lexical overlaps require manual review of remaining diff; they do not establish runtime conflicts.
