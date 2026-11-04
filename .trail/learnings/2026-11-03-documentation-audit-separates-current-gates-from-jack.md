---
title: Documentation audit separates current gates from historical evidence
author: Jack
date: 2026-11-03
branch: docs/codebase-alignment-audit
touches:
  - AGENTS.md
  - docs/application-profiles.md
  - docs/build-install-and-nightly.md
  - docs/developer-guide.md
  - docs/experimental-blade-reference.md
  - docs/experimental-blade-support.md
  - docs/fan-power-source-policy.md
  - docs/keyboard-input-backends.md
  - docs/keyboard-lighting-file-format.md
  - docs/localization.md
---

## Decided
Audit maintained guides against the current canonical main revision, while preserving dated capture results, published changelog snapshots, archived implementation logs and unmerged work as separate evidence. The 2026-11-03 audit used b9e624755c0c2c844688562fb5cb40225825730b (0.50.3), covering 68 project Markdown documents plus the two local-Nightly skill guides.

At that revision, service IPC is 72, exact experimental models enable in Stable and Nightly, and SnapTap offers explicit Windows-hook or Razer-driver choices. Driver setup searches Windows Update first; pinned packages are only the successful initial no-offer fallback. Blade 18 admits Balanced host curves and Silent fixed fans; only Blade 18 needs Automatic before Custom, while the 2023 Blade 14/16 preserve readable fixed fans. General firmware-provenance wording must distinguish unrestricted BIOS provenance from the actual RZ09-0482/RZ09-0483 HyperBoost minimum-MCU gates in ExperimentalBladePort.HyperBoost.cs.

## Tried and ruled out
Do not promote unmerged reliability or THX work into current behavior, turn source-backed capabilities into physical-validation claims, or rewrite immutable historical observations to match newer software. A closed issue does not prove a runtime gap was implemented: installer non-full completion still exits without launching the Tray, so issue #25 is cited as the original report; completed roadmap #532 is marked historical.

## Watch out
The audit's Trail check reported 20 lexical matches, not semantic reversals. MCU provenance does not freeze product versions. Store wording does not add inbox sidecar-catalog requirements. THX summaries retain trusted installed API admission and report existing physical evidence rather than new write probes. Explicit input-method selection is not automatic failure fallback or per-device Windows-hook suppression; the shared supervised helper, ownership journals, exact-model ABI and Windows Update readback gates remain authoritative. No code, version, firmware commands, hardware authority or runtime admission changed.

Keep English, Chinese and Russian guides aligned when current controls, setup and Chroma maps change. Validate local file links and heading anchors, UTF-8/CRLF endings, and the CI impact plan; docs can select source-reading build contracts even when no .NET project needs rebuilding.
