---
title: Offline Blade firmware CLI and extensible register explorer
author: Jack
date: 2026-11-05
branch: feat/battery-saver-low-fan-validation
touches:
  - CHANGELOG.md
  - docs/so6120-ec-register-semantics.md
  - ".github/workflows/firmware-tools.yml"
  - tools/firmware/.gitignore
  - tools/firmware/README.md
  - tools/firmware/blade_firmware/__init__.py
  - tools/firmware/blade_firmware/assets/explorer.css
  - tools/firmware/blade_firmware/assets/explorer.js
  - tools/firmware/blade_firmware/cli.py
  - tools/firmware/blade_firmware/core.py
---

## Decided

Keep reusable offline EC/MCU analysis in tools/firmware. The standard-library Python CLI writes deterministic JSON and a self-contained HTML explorer. Data-only profiles bind exact carrier, payload and decoded-image hashes; they are interpretation metadata, never production firmware allowlists or hardware authority. Other models can supply independent bank geometry or explicit internal-field annotations. Unknown images retain metadata without borrowed meanings.

## Tried and ruled out

Do not execute update carriers or ship vendor payloads. The reviewed NSIS block/LZMA layout and strict Intel HEX parser are enough for SO6120 EC 1.04 and MCU 1.07. A per-byte dictionary for sparse HEX scales poorly; bounded image/population bytearrays avoid that overhead. Windows no-clobber publication uses atomic os.rename because hardlinks were denied in the managed workspace; POSIX uses an exclusive link. A local browser preview was blocked by browser policy, so tests and source review do not claim visual validation.

## Watch out

Keep bank metadata_size independent of address_size. SO6120 manual limits use 100 RPM/count while automatic thermal targets use 50; sensor bounds remain raw. The EC's 2048 mappings and 32 tables with 344 stages match the earlier decoded artifacts, but factory tables are not live state. Validate nested report data before render/diff and keep embedded JSON escaped with CSP and text-only DOM insertion. Preserve bounded parsing, exact-hash mismatch rejection, no-overwrite outputs and synthetic second-model tests. Hardware read/write transport, register ownership and restoration remain separate unresolved contracts.
