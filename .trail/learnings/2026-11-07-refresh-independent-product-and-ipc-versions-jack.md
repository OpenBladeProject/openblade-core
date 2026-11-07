---
title: Refresh independent product and IPC versions before CPU merge
author: Jack
date: 2026-11-07
branch: feat/cpu-voltage-completion
touches:
  - AGENTS.md
  - CHANGELOG.md
  - Directory.Build.props
  - README.md
  - README.ru-RU.md
  - README.zh-CN.md
  - docs/application-profiles.md
  - docs/build-install-and-nightly.md
  - docs/cpu-validation-tools.md
  - docs/developer-guide.md
---

## Decided
Before merging the CPU integration, refresh current main and recalculate product and IPC versions independently. Main d49eb99a already shipped product0.52.0 and IPC73 for new presets; CPU's independent unpublished73 had a different shape. The combined integration is0.53.0/IPC74 with read-only51 retained. Keep main's historical73 golden and add the reviewed union74 golden. CPU adds no persisted profile fields, so retain main's profile23/export15 schemas.
## Tried and ruled out
Do not keep a duplicate product bump or reuse either conflicting73 manifest. The74 manifest minus only CPU DTOs, request discriminators and optional CPU fields hashes exactly to main's73 manifest. Strict frozen51 tests must include nonnull CPU status and result separately because responses allow exactly one body. Do not append every key missing from new main when resolving resources: preserve actual CPU additions from the merge base, not older non-CPU strings main intentionally retired.
## Watch out
Merge authorization does not prove the original laptop's cleanup or historical PPT restoration. Keep those obligations in622. Preserve main's18-model definitions, localization replacements and all CPU safety fixes. After refreshed exact-head checks pass, merge with expected-head protection, preserve needed ignored test evidence before worktree archival, prove branch ancestry, delete only the merged branch and fast-forward the primary checkout without touching unrelated files.
