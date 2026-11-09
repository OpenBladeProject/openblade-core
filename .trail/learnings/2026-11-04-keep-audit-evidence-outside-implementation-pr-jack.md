---
title: Keep audit evidence outside implementation PR diffs
author: Jack
date: 2026-11-04
branch: fix/codebase-reliability-audit
touches:
  - build/benchmarks/BackgroundWorkloads/RESULTS.md
  - build/benchmarks/BackgroundWorkloads/results/2026-11-03-measurements.json
  - build/benchmarks/NativeResources/RESULTS.md
  - build/benchmarks/NativeResources/results/2026-11-03-baseline-series.csv
  - build/benchmarks/NativeResources/results/2026-11-03-candidate-series.csv
  - build/benchmarks/NativeResources/results/2026-11-03-candidate-summary.json
  - build/benchmarks/NativeResources/results/2026-11-03-controls.json
  - build/benchmarks/NativeResources/results/2026-11-03-lifetime-comparison.json
  - build/benchmarks/NativeResources/results/2026-11-03-offscreen-desktop-comparison.json
  - build/benchmarks/NativeResources/results/2026-11-03-offscreen-guard-validation.json
---

## Decided
Keep implementation PRs focused on code, regression tests and concise validation summaries. Preserve bulky per-cycle benchmark JSON/CSV and the full investigation report in an immutable source snapshot retained by an archive branch; link the exact commit from the summary. The reliability audit's original evidence remains at 0ba5840e45fa6ba80183ab1a2c9c397295f3b99f on chore/reliability-audit-evidence-20261104.

## Tried and ruled out
Bundling repeated controls and attribution ledgers with every fix added more than 17,000 evidence lines to PR874 and made review harder. Deleting the evidence or substituting a moving branch-only link would lose provenance. Historical measured revisions must not be presented as fresh validation of extracted PR heads.

## Watch out
Keep executable harnesses and meaningful regression tests. Split whole final files along verified dependency boundaries, not mixed historical commits. Core recovery and shared contracts precede the tray slice; provisioning and developer tooling can be reviewed independently. Preserve existing learning contents, archive paths and artifact hashes. Recalculate each PR's version against its target, link its own changelog bullets, and verify extracted heads before marking them ready.
