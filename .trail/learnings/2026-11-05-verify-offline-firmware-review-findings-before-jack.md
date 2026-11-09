---
title: Verify offline firmware review findings before changing boundaries
author: Jack
date: 2026-11-05
branch: feat/battery-saver-low-fan-validation
touches:
  - ".github/workflows/firmware-tools.yml"
  - CHANGELOG.md
  - docs/so6120-ec-register-semantics.md
  - tools/firmware/.gitignore
  - tools/firmware/README.md
  - tools/firmware/blade_firmware/__init__.py
  - tools/firmware/blade_firmware/assets/explorer.css
  - tools/firmware/blade_firmware/assets/explorer.js
  - tools/firmware/blade_firmware/cli.py
  - tools/firmware/blade_firmware/core.py
---

## Decided

The Claude advisory review of the offline firmware tool found reproducible boundary bugs: colon-leading unknown raw binaries were mistaken for HEX; semantically equal optional register defaults produced false report differences; filtered or paginated-out selections left stale details; inspect omitted the extracted image from its success output. Add regressions and fix these shared offline paths. Keep explicit HEX extensions strict, require successful full decoding for unlabeled HEX detection, compare canonical copied register records, and clear detail selection when its row is absent from the visible page.

## Tried and ruled out

Do not accept every advisory comment as a defect. Official setup-python documentation confirms v7 exists. Revalidating public API inputs and retaining authoritative exclusive publication are intentional boundaries. Two-pass HEX parsing trades repeated CPU work for bounded memory. POSIX owner-only output is intentional for private firmware artifacts; widening permissions automatically is unnecessary.

## Watch out

POSIX no-clobber atomic publication requires hard-link support. Report unsupported filesystems clearly and document the requirement instead of silently replacing the publication with a partially visible copy. Windows uses no-replace rename. Trail check's matches in README prose include negated carrier execution, offline image hashes and unrelated runtime/UI terminology; these are lexical overlaps, not a change to production hardware admission. Record that distinction in the commit and PR. Claude's frozen review precedes the fixes; focused regressions and a separate Codex review verify the follow-up, not an implied second Claude pass.
