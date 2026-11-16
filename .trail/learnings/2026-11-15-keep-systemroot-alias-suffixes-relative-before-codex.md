---
title: Keep SystemRoot alias suffixes relative before combining
author: Codex
date: 2026-11-15
branch: fix/pawnio-install-diagnostics
touches:
  - src/OpenBlade.Platform/PawnIoRuntimeAdmission.cs
  - tests/OpenBlade.Platform.Tests/PawnIoRuntimeAdmissionTests.cs
---

## Decided
Reject rooted suffixes after either SystemRoot alias before Path.Combine. Otherwise a malformed percent-SystemRoot or kernel-SystemRoot string followed by an absolute C: path discards the Windows-provided root. The existing kernel-SystemRoot branch shared this grammar defect, so the guard belongs in a shared resolver for both forms.
## Tried and ruled out
Independent review found this after the two additional valid aliases were implemented. Focused regressions reproduced two improperly admitted absolute-suffix cases; root-relative suffix cases were already refused downstream. A shared Path.IsPathRooted check now rejects both kinds before combining. All213Platformtests pass. Keep valid aliases, package containment, catalog trust, device identity and service admission intact; this adds no native operation or hardware authority.
Do not infer the reporter's unknown ImagePath from any malformed-path fixture. The separate installed-file before/after reproduction proves a real alias-normalization bug with identical signed file bytes, not exact reporter recovery. No service configuration or driver installation was changed to produce these results.
## Watch out
Literal alias expansion must never become generic process-environment expansion or a way to replace the trusted Windows directory. Reuse this rule for every supported SystemRoot spelling. The new rooted-suffix regression is a shared software fix for all models, not a model-specific gate. Main advanced through PR974 to0.64.1 during investigation; this PR's final patch version must be0.64.2 after merging that base. Trail lexical matches involving historical version digits or unrelated hardware writes do not change these bounds.
