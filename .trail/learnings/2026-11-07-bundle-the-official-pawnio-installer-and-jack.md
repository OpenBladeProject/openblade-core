---
title: Bundle the official PawnIO installer and scope Intel IPF coverage
author: Jack
date: 2026-11-07
branch: feat/battery-saver-low-fan-validation
touches:
  - docs/so6120-cpu-temperature-source.md
---

## Decided
Bundling LibreHardwareMonitor plus the official PawnIO installer is feasible without a separate user download. Keep MPL-covered source and license notices available, including separate embedded-module notices. The PawnIO author's binary redistribution permission covers the unmodified installer; it is not permission to repackage a loose signed SYS as a private driver. Silent setup is supported and 2.2.0 reports restart-needed results. A future OpenBlade setup should reuse a compatible existing installation and preserve the shared driver on uninstall. This research adds no dependency or installation authority; see docs/so6120-cpu-temperature-source.md for primary sources.

## Tried and ruled out
The working Intel public C API is not a universal modern-Intel temperature API. It requires compatible installed ipfcore.dll/ipfipc.dll, the Intel service and platform/processor driver stack, and an enabled CPU participant implementing the reviewed temperature GET. Intel documents IPF/DTT as OEM- and firmware-configured. Only the RZ09-0581 / Core Ultra 9 386H combination has been exercised. A recent CPU generation or copying the two DLLs alone does not establish coverage; AMD requires a separate provider.

## Watch out
Distinguish open-source PawnIO source licensing from redistribution of the official signed binary installer. Preserve shared-driver lifetime, verify setup results and handle restart requirements. Do not treat feasibility research as a runtime backend, installer change, or waiver of the repository's no-new-driver rule. The Intel prototype remains diagnostic-only; compatible API admission should not become a binary-version allowlist.
