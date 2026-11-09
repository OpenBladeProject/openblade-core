---
title: Reject constant TZRZ CPU telemetry and review the standalone IPF candidate
author: Jack
date: 2026-11-07
branch: feat/battery-saver-low-fan-validation
touches:
  - CHANGELOG.md
  - docs/so6120-stop-auto-guardian.md
  - src/OpenBlade.Service/FanCurveControl.cs
  - tests/OpenBlade.Capture.Tests/FanStopThermalPolicyTests.cs
  - tests/OpenBlade.Service.Tests/FanCurveCalculatorTests.cs
  - docs/so6120-cpu-temperature-source.md
---

## Decided
TZRZ is not a CPU measurement on reviewed RZ09-0581 BIOS4.01 and RZ09-0528 BIOS2.02: both _TMP methods return literal0x0C6E (45.05 C). Import correction07bd37c0 at the shared WindowsThermalZone reader. Reject those sources, not the value45. Missing CPU temperature refuses manual/automatic Stop entry, ends an owned Stop immediately even with a long custom delay, and retains CPU-dependent fan curves' existing maximum-speed fallback. Preserve the branch's independent GPU inactivity/session handling.

## Tried and ruled out
The SO6120 EC obtains real PECI measurements, but host-visible cached CPU bytes retain old data on failures without a reviewed CPU-specific generation/age signal. Re-reading the cache cannot prove producer freshness. The installed Intel IPF driver provides a stronger standalone candidate: GET14 D0/255 obtains a new ACPI/MMIO/MSR result, propagates failure and does not return its historical maximum. Reviewed metadata GET5/6 bind participant ID/state/DSP and PCI/ACPI identity. This is candidate evidence, not production Stop admission; docs/so6120-cpu-temperature-source.md records the pinned source and remaining limits.

## Watch out
Do not import core MSR0x19C Reading Valid bit31 into package MSR0x1B1; Intel reserves bit31 in the package register. Do not silently mask or correct the converted vendor result. Fresh native completion is distinct from producer freshness. A future provider must not let a blocked driver read prevent guardian Auto restoration. Model/register semantics and independently installed driver API admission remain exact; diagnostic hashes are provenance, not production version allowlists. No new monitoring dependency or kernel driver was added.
