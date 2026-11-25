---
title: Native setter admission brackets each 07/8C query with Windows samples
author: Jack
date: 2026-11-25
branch: feat/native-hyperboost-battery-exit
---

## Decided
- PowerFence::before_query samples Windows before every performance and fan setter's 07/8C query; admits refuses a post-reply Windows kind that differs. This is managed AdmitOrdinaryThermalCommandAsync (capture before :26, compare after :55). Releasing the admitted source after each SET had removed the only pre-query Windows look.
- The pre-query refusal rule is thermal_mutation::admits_windows (AC admits all, battery only a battery preset, unknown none), equal to "some class admits the destination".
- Fans share the fix: managed brackets fan commands the same way.

## Tried and ruled out
- Treating the pre-sample as redundant: with the post compare, outcomes match; only the query count differs. The test asserts power_reads to keep it.
- F7 (class rule bypassed before destination_holds) is not equivalent: a battery reading under Windows AC would retire an AC temporary.

## Watch out
- The fixture derives Windows from the adapter level, so change_power at a read changes Windows across that query and now stops the setter. Use change_power_after_write for an unplug between setters.
- A carried battery preset on a fan return needs Windows AC in native; managed admits its selectors on battery. Documented deviation.
