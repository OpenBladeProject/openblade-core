---
title: Cooling-pad Automatic looks flat at normal temperatures
author: Jack
date: 2026-11-22
branch: claude/cooling-pad-auto-mode-temps-39ca29
cost: { minutes: 1, tokens: 92042 }
---

## Decided
Nothing changed yet. A report that pad Automatic "does not respond to CPU and GPU temps" comes from the Synapse step tables in CoolingPadAutoCurves, not from the runtime loop. MaintainAsync re-reads temperatures every second and resends when the target changes. With step lookup (the speed of the first point above the reading, no blending), Balanced is 2200 RPM for CPU 40-89 C and GPU 30-79 C, Performance is 2700 RPM once the CPU passes 55 C or the GPU passes 45 C, and only Quiet changes in normal use (at 50 and 85 C CPU). The proposed fix, waiting on the user: keep the Synapse points but interpolate between them, as the manual pad curve does (CoolingPadControlRuntime.Evaluate). It uses the same 0D/01 command and range, and it must be documented as a further difference from Synapse.

## Tried and ruled out
Suspecting the maintain loop, the temperature filter or the GPU-off handling. All of them feed the tables correctly. Re-checking the vendor bundle's step semantics is also unnecessary: a 3-point step table is flat at laptop temperatures whichever way it is read.

## Watch out
A Blade CPU almost never drops below 40 C, so a Balanced test always reads 2200 RPM. Check behavior with Quiet, or across the 55 C CPU threshold in Performance.
