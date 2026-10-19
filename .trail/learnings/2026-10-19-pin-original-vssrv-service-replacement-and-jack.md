---
title: pin original VSSrv service replacement and COM registration contract
author: Jack
date: 2026-10-19
branch: feat/thx-driver-provisioning
touches:
  - docs/thx-installation-investigation.md
---

## Decided

The original legacy VSSrv -service path supplies a source-backed service/COM registration contract without invoking Synapse. Treat it as a mutating replacement operation: pre-registration 14005AFF0 stops and deletes an existing VSSrv, even after a reported stop failure. Keep the legacy fresh-install block until original endpoint FX binding and installation/readback are established; this trace does not justify enabling a repair/retry command.

## Tried and ruled out

Traced parser 140056500, REGISTRY/101 application, APPID substitution, service creation and automatic-start configuration, object-map callbacks, and type-library registration. All nine class/category callbacks share a verified null-return stub. The module substitution callback adds only APPID; known HKCR templates supply COM classes, with no concrete endpoint FX source. Type-library loading first uses the executable and can fall back to VSSrv.tlb. The four previously unresolved registration dependencies are now resolved to this positive contract; no broad audio-runtime trace was needed.

## Watch out

Static template presence differs from registration success and from endpoint binding. The optional post-registration callback is image-null only, and sidecar type-library contents are dynamic. VSSrvInit ignores the child exit code and uses unbounded waits; Windows can defer AddSoftware. A successful INF import or initializer exit cannot prove readiness. Future setup needs independent service, COM and endpoint readback and stop-on-ambiguity behavior. No vendor process, registration, driver installation or live state mutation was performed in this investigation.
