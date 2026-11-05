---
title: SO6120 runtime traces narrow callers without proving mailbox exclusion
author: Jack
date: 2026-11-05
branch: feat/battery-saver-low-fan-validation
touches:
  - docs/so6120-mailbox-runtime-observation.md
  - docs/so6120-host-mailbox-contract.md
  - docs/so6120-acpi-vendor-transport.md
---

## Decided

Document the two parallel runtime investigations in docs/so6120-mailbox-runtime-observation.md. The exact installed IPF DSP catalog has 35 hash-checked records, 2,185 primitives and 360 ACPI actions, with no literal TBCC/ECRR selector. One passive Kernel-Acpi trace recorded 112 method events and no TBCC/ECRR name. Its missing nested ADBG/GGOV events and absent invocation/result fields prevent treating silence as unreachability or a caller chain.

A separately authorized Handle-only System Object ETW snapshot can collect handle rundown without opening InpOut, duplicating foreign handles or adding a driver. The corrected run stopped cleanly, reported zero loss and yielded 238,555 version-2 handle records. Validate PID/handle/type/name against a held own-file marker and the original payload before attribution. This run had 21,067 File handles, including 947 unnamed; no rundown name contained inpout. This is bounded observation, not complete client exclusion.

## Tried and ruled out

Extended-handle object pointers were redacted under the ordinary host token. Broad foreign File-handle duplication was not used because File handles may include sockets. Signed Microsoft Handle 5.0 attempts PROCEXP152 driver initialization before query filters; do not run it under a no-new-driver scope. The first General+Handle trace saturated its 128 MiB cap and lost events without retaining handle rundown. Removing General, increasing requested buffers and immediately stopping produced a usable snapshot after separate approval; never repeat a one-run UAC authorization silently.

## Watch out

Keep raw ETL, object names, process identities, vendor binaries and diagnostic helpers in ignored local artifacts. Report strict decoder and controller loss/cleanup outcomes, and reject unknown event layouts instead of trusting names or generic rundown markers. A regular-file positive control validates its decoded layout, not universal names, transient clients, AML/SMM exclusion or an ownership lease. Existing nonexclusive InpOut access and quiet traces do not resolve transaction integrity, independent applied readback or bounded restoration. Do not invoke firmware, open a port driver or claim PWM-test authority from this research.
