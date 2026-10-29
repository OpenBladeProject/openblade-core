---
title: trace legacy THX policy and session boundaries without inventing FX binding
author: Jack
date: 2026-10-21
branch: docs/legacy-thx-binding-followup
touches:
  - docs/thx-installation-investigation.md
---

## Decided

The legacy THX installation gap remains an original exact-model endpoint FX binding plus install/rescan/restart contract, tracked in #833. The recovered signed THXHDAudio A/C sections declare the three supported codec children and endpoint device/config metadata, but not the complete host/offload FX chain. Keep fresh legacy setup Blocked/CandidateOnly/installableCandidate=false; modern Synapse-free runtime reports do not validate it.

## Tried and ruled out

The actual Razer Drivers & Downloads tables were loaded on 2026-10-21 for RZ09-0482/0483/0484: no separate Realtek/THX installer was listed. A Blade 16 owner post corroborates the THXHDAudio export folder but supplies no package bytes or exact installation provenance. Microsoft extension targeting means an ExtensionID or broadly advertised base codec CAB cannot establish model applicability, and public Catalog negatives do not exclude targeted Windows Update delivery.

In signed RTKVHD64.sys 6.0.9909.1 (SHA 48C525F8E58DA40EE54BAE8EA95C44D6944A9D77E9EE98C500B90284257312CD), the AMD E82 GUID at file 0x1BD0B0 flows through 0x14003B016 to 0x140032064 as an internal HAPEnableTHX policy record. SSTPPCfg reader 0x1405E6EEC reads policy into state. Neither inspected path supplies the missing endpoint FX values. VSSrv startup writes THX Discovery connection data; reviewed VSHelper endpoint property stores use STGM_READ/GetValue and forward typed device/jack state to VSSrvAudioSession. These bounded results are not proof about every runtime or dynamic path.

## Watch out

Pin the exact static input before reusing native analysis. VSSrv 2.6.5 has at least two distinct binaries: 2,430,000-byte DC709A34D2CB043505B200316B4CA1A51B9AE1FC917B55DD804D5F30841C868F and 2,430,008-byte CE43B98118DEAAE11DC619A39AFC2F35623FD4C5E9717AE8D9D3C8504BF684E2. The correct VSHelper is 310,840-byte B1708DCA72A4392A1B4005570CCD6CFE36E7AC5454A1B34CBB303FCE64BD22F0; the older LegacyHelper Ghidra project instead analyzes thxsahelper.exe. Do not attribute its bodies to VSHelper. Static artifact hashes here identify evidence, not runtime driver allowlists. No vendor code or live COM was executed; do not invent endpoint registrations or infer setup from state-update method names.
