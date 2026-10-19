---
title: Recover rznb12 identity before adding an acquisition source
author: Jack
date: 2026-10-19
branch: feat/thx-driver-provisioning
touches:
  - docs/thx-audio.md
  - docs/thx-installation-investigation.md
---

## Decided

Read-only Windows Installer cache inspection recovered the original rznb12 filename THXAPO_rznb12_v4.2.37.1621-5117e8e-i2546a_MSHLKSIGN-2255eea3.3219a439.exe. The cached executable is 53,720,208 bytes, SHA-256 32392A63FC704DFC4BFBE4F33ED038773BBDE21AC88C61BAEEA013E3F98E8CF2, with valid THX LTD. Authenticode. Static MSI cabinet extraction yields three signed packages (APO, extension, service); all nine non-catalog files pass signed-catalog membership verification. The extension binds the 30101A58 SoundWire subsystem and SO6120_speakers-default. Its properties archive has 340 entries and SHA-256 FF587D74CE7763FCB2B0150B62A67237844B688647CE0434219B814608E12C07. Keep this as recovered package evidence until an acquisition source and installer behavior are reviewed.

## Tried and ruled out

The official Synapse installer loader exposes installer-manifest.json and advertised archive paths. All seven product-736 manifests 1.0.0 through 1.0.6 omit a base APO installer. The advertised legacy histories (669:1.0.2, 671:1.0.0-1.0.1, 672:1.0.1) omit THX/audio resources. Current beta736 and production737 add no base source. The exact recovered filename at the known common THX directory returned HEAD403; the directory was inferred, so this is unusable and inconclusive rather than proof of absence. The inspected registration did not retain the original source URL. See docs/thx-installation-investigation.md for the source links.

## Watch out

The earlier learning's missing rznb12 base installer means a usable public acquisition source remains missing; a signed older package now exists in local cache evidence. Version4.2.37.1621 is not the pinned4.2.41.2499 runtime. This discovery does not admit another driver build, establish successful installation, authorize proprietary redistribution, or make generic legacy THX components a complete OEM package. Preserve observational discovery and exact-model pins. No vendor executable was run during this research.
