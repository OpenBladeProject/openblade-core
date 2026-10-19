---
title: Review every catalog artifact when supported models change
author: Jack
date: 2026-10-19
branch: feat/thx-component-download
touches:
  - AGENTS.md
  - Directory.Build.props
  - docs/thx-audio.md
  - src/OpenBlade.Contracts/RazerBladeModelCatalog.cs
  - src/OpenBlade.SessionAgent/ThxAudioAdmission.cs
  - src/OpenBlade.SessionAgent/ThxAudioNativeAdapter.cs
  - src/OpenBlade.Tray.Core/Properties/Resources.qps-ploc.resx
  - src/OpenBlade.Tray.Core/Properties/Resources.resx
  - src/OpenBlade.Tray.Core/Properties/Resources.ru-RU.resx
  - src/OpenBlade.Tray.Core/Properties/Resources.zh-CN.resx
---

## Decided
The applicability policy covers every catalog artifact and every OpenBlade-supported model. Keep one metadata ledger in blade-driver-catalog with an Applicable or NotEstablished evidence-backed decision per artifact/model pair. Core exposes its authoritative model list read-only and tests the reviewed offline ledger against it, so future models require catalog review. Descriptor applicability and runtime THX artifact pins must match the reviewed metadata.

## Tried and ruled out
Byte-identical common dependencies do not make full driver stacks universal: the existing stacks retain PID-specific USB/HID/virtual INF matches. THX 1.1.1.0 is shared by exact vendor products 709, 710 and 736, but the other four supported products have no THX resource in their reviewed manifests. Do not infer either universal support or incompatibility from missing evidence.

## Watch out
Catalog coverage is a maintenance contract, not new runtime admission. Review all artifacts when adding models, retain pinned vendor revisions and exact identities, and refresh metadata-only test fixtures from an immutable catalog commit. User feedback also called out awkward Chinese THX wording; review Russian alongside Chinese using natural audio terminology and inspect rendered localized pages after catalog regeneration.
