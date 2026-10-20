---
title: Accept compatible installed driver API updates without build hash allowlists
author: Jack
date: 2026-10-20
branch: feat/thx-driver-provisioning
touches:
  - AGENTS.md
  - CHANGELOG.md
  - docs/blade18-source-feature-recovery.md
  - docs/razer-brightness-driver-package.md
  - docs/ru-RU/razer-brightness-driver-package.md
  - docs/snap-tap.md
  - docs/thx-audio.md
  - docs/thx-installation-investigation.md
  - docs/thx-legacy-interface.md
  - docs/zh-CN/razer-brightness-driver-package.md
---

## Decided
The maintainer explicitly replaced runtime exact-build admission for API-based driver access. Compatible installed THX, SnapTap and native-brightness driver updates must work without adding hashes or shipping another OpenBlade release. AGENTS.md is authoritative: preserve exact model, device, topology, installed package/service binding, Windows catalog trust and publisher, file membership and typed API/ABI contracts. Signer identity comes from successful trust-provider state. New observed content invalidates cached runtime state rather than failing a release-maintained allowlist.

## Tried and ruled out
Do not extend the old binary hash lists for every new reporter payload, infer incompatibility from a changed hash, or treat a matching version as API evidence. Do not remove artifact hash checks: downloads, exports, catalog assets, installation recipes, transaction recovery and OpenBlade-owned executable components still require exact artifact verification. Do not invent write probes where the API has no read-only capability query.

## Watch out
This policy supersedes earlier learnings that described pinned installed driver bytes as runtime admission. Source hashes remain valuable provenance; installer pins still select exact immutable recipes. Same API does not establish physical validation of every future build. Preserve fresh pre-write admission, typed pre-read/readback where available, ownership and stop-on-ambiguity. Legacy endpoint binding and clean THX installation remain separate unresolved evidence; no model, raw firmware command or development-time hardware authority is added.
