---
title: separate legacy THX runtime consumers from initial FX provisioning
author: Jack
date: 2026-10-26
branch: docs/legacy-thx-binding-followup
touches:
  - docs/thx-installation-investigation.md
---

## Decided

The legacy THX continuation consumes pre-existing endpoint/configuration state and updates an already-created output APO. It does not recover the initial Windows FX association or installation recipe. In pinned service DC709A34... the FastEqualizer continuation reads a JSON file and traverses SpeakerPath/HeadphonePath targets. In the separately pinned07c8 AVX2 APO (SHA49962CC201A76B5BCA85B5D2E9DA60A68D2DB3B076AA481F629418118EA350DE), LockForProcess180010810 starts CreateThread at180012598 with thunk180022BE0, leading to AsynchronousEventLoop18001EBB0. Assembly offsets match the type library's IVSSrvTHXOutMFXAPO getters. These getters update processing/VAD state after APO creation.

## Tried and ruled out

Initialize180014740 reads APOInitSystemEffects2.pAPOEndpointProperties at+0x18 with GetValue, including vendor GUID D5E8F0AB-4DE6-4D91-AB21-68868DDA6A4A PID0(device classification) and PID1(configuration path). Preserve those original values/configuration with binding evidence; do not synthesize a setup rule from runtime reads. No initial FX writer was found in the reviewed continuation. Other variants, input/settings consumers and recursive library/registration paths remain unreviewed; initial automatic analysis was time-bounded and consequential call offsets were checked in assembly.

Current Windows catalog policy is a separate check from artifact hashes and generic catalog signatures. On Windows10.0.26300.0, unmodified WindowsDriverApiTrust blob5999926595203ac7f1e2cb2adce2bca880166d93 accepted7 recovered catalogs and18 selected members, with verified Microsoft HW publisher. Independent default-driver-policy SignTool /o2:10.0.26300 passed oneINF per catalog. The extension, service, both APO packages, SCU, HSA declaration and selected Realtek roots/SYS/DAT have no additional trust blocker on that host.

## Watch out

Fresh legacy installation remains Blocked/CandidateOnly/installableCandidate=false under issue833. Trust success does not establish applicability, a combined recipe, child creation, installation order or audible processing;26300 is a test condition, not a minimum OS floor. HSA's empty SourceDisksFiles and Store PFN do not verify/acquire the app. Realtek coverage is the two exact Razer roots plus RTKVHD64.sys/RTAIODAT.DAT, not the entireCAB. Both APO variants are trusted individually, but only07c8 was traced. No vendor code was executed or live COM/device/service/audio state changed. Research artifact hashes are evidence identities, never runtime update allowlists. Preserve the original binding producer and signed packages as the acquisition target.
