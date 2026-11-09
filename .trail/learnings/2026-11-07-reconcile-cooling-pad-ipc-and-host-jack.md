---
title: Reconcile cooling-pad IPC and host scope with newer main
author: Jack
date: 2026-11-07
branch: feat/cooling-pad-source-support
touches:
  - AGENTS.md
  - CHANGELOG.md
  - Directory.Build.props
  - OpenBlade.slnx
  - README.md
  - README.ru-RU.md
  - README.zh-CN.md
  - build/Capture-LocalizationSnapshots.ps1
  - build/CiImpactManifest.json
  - build/Export-LocalizationTranslatorPackage.ps1
---

## Decided
When merging cooling-pad PR889 after main0.55.0/IPC75, use product0.56.0 and a newIPC76. Retain main's historical73/74/75 manifests; the76 manifest with only the new pad DTOs, five request discriminators and optional status removed hashes exactly to the reviewed main75 shape. Preserve both CPU and accessory service composition/lifecycle paths. Preserve deployed Labs=7; append the unshipped cooling-pad destination separately.

## Tried and ruled out
Do not let ExperimentalBladeModels.All automatically extend cooling-pad native admission as the laptop catalog grows. Use the two explicit validated host definitions and the original five exact experimental model/PID pairs. Other source-reviewed laptops need separate accessory admission. Do not bring back the shared firmware CPU reading after main rejected it as constant; CPU and combined pad curves must retain missing-input refusal, with the GPU-only alternative documented.

## Watch out
Resolving resource conflicts means adding actual cooling-pad additions to current main, not restoring strings main intentionally removed. A clean marker resolution alone can still drop Labs from current navigation defaults or overwrite a numeric navigation ID. Verify current/default migrations, connected-only page/search visibility and hardware-free native rendering. Original cooling-pad CI is not evidence for this newly combined source; run fresh full verification and review, then use expected-head merge protection and complete safe worktree/branch cleanup.
