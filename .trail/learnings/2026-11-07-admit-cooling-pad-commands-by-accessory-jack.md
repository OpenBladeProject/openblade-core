---
title: Admit cooling-pad commands by accessory identity rather than laptop model
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
Cooling-pad writes are admitted by the accessory's own reviewed USB identity and command contract, not by the laptop manufacturer/model/SKU/BIOS. The user questioned that coupling during PR889 integration; there is no source-backed pad firmware prerequisite tying these commands to one Blade host. A separate pad-only opener uses the existing strict serialized session, exact1532:0F43/revision0200/MI00/consumer0C:1/91-byte identity, reportID0, fresh controller/device admission, protected ownership, session/power guards and typed readback. Laptop openers retain their own exact-host requirements.

## Tried and ruled out
This supersedes the seven-host restriction in the earlier Reconcile cooling-pad IPC and host scope with newer main learning. Neither expanding ExperimentalBladeModels.All nor a hardcoded seven-host subset is an appropriate source of accessory authority. Do not fabricate IHostIdentitySource success or HostIdentityEvidence to reuse a laptop opener. Keep the accessory opener separate and reject unreadable, ambiguous, mismatched or replaced pad candidates; fallback is limited to known access-denied handle opening before firmware I/O.

## Watch out
Host independence does not admit new pad firmware commands or relax laptop controls. CPU/GPU curves still need genuinely fresh readings, keyboard sync still needs completed host-rendered keyboard frames, and the pad remains source-backed experimental pending physical validation. Hardware-free regressions prove pad Apply/Release works when no laptop backend is admitted while laptop writes remain blocked, and strict native per-SET identity/controller revocation still prevents writes.
