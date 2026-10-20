---
title: resolve Razer OEM name callbacks and deferred THX initialization
author: Jack
date: 2026-10-19
branch: feat/thx-driver-provisioning
touches:
  - docs/thx-installation-investigation.md
---

## Decided

Correct the previous codec-family attribution: RTKVHD64.sys 6.0.9909.1 adapter+C8C is an OEM/subsystem-table dispatch. Row915 at14024F1A0 matches device+15C's high word1A58, selects dispatch2A/id22, and creates the Razer override through140475C14/140473E6C. Its vtable1401D3100 name slots38/40 both target14000FCD0, verified bytes33 C0 C3, so they return null and use base name selection. These callbacks do not resolve the missing original FX binding.

## Tried and ruled out

Factory-era6.0.9601 had already been inspected; do not redownload it as a new binding lead. Statically decompiled original VSSrvInit from the catalog-pinned legacy service package: it runs System32 VSSrv.exe -service without a shell, waits, then starts/waits for VSSrv, with two-second pauses. The wrapper itself reads only its optional log registry value. VSSrv REGISTRY/101 contains HKCR COM templates, not endpoint FX declarations. The delegated native -service branch remains untraced; neither static resource presence nor wrapper code proves its actual registration behavior.

## Watch out

Codec HWID proves device+160, whereas OEM selection uses+15C; initialization copies only if+158 is zero orFFFFFFFF. Do not turn the Razer override's proven behavior into universal live selection or endpoint-flag proof. VSSrvInit has unbounded process/service waits and ignores the child exit code. Microsoft's AddSoftware contract defers execution without guaranteeing when or whether it runs. A future fresh-setup recipe needs independent service, COM and endpoint readback; a successful INF import or helper exit is insufficient. Preserve the legacy install block and exact-model original signed artifacts. All analysis here was static; no driver, service, registry, audio or vendor process was changed or executed.
