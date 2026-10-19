---
title: Missing THX client is separate from installed APO drivers
author: Codex
date: 2026-10-19
touches:
  - src/OpenBlade.SessionAgent/ThxAudioAdmission.cs
  - src/OpenBlade.SessionAgent/ThxAudioCompatibility.cs
  - docs/thx-installation-investigation.md
---

## Decided
The current official Synapse middleware manifests for products 709 (RZ09-0530), 710 (RZ09-0528 / Blade 16 2025), and 736 (RZ09-0581 / Blade 16 2026) separately declare the THX native client as ThxV4Native_v1.1.1.0.dll under resource path thxv4common. All three pin SHA-256 8E8F6923624762935D70B72F58BEC00808DB84DF740340047919B53331130A0A. On 2026-10-19, reading the official DLL URL as data returned 13,684,872 bytes with that same hash; no DLL was saved, loaded or installed. The client DLL is a separate Synapse resource from the APO driver installer. Codec/DriverStore presence does not establish client presence.

Sources: https://apps.razer.com/synapse/products/709/mw/manifest.json ; https://apps.razer.com/synapse/products/710/mw/manifest.json ; https://apps.razer.com/synapse/products/736/mw/manifest.json ; https://app-assets.razer.com/files/synapse/devices/common/THX/ThxV4Native_v1.1.1.0.dll . These live snapshots can change.

## Tried and ruled out
No Blade-specific rename was established. A first-hand headset report mentions native version 1.1.3.0, but that is not evidence to admit a new Blade DLL. Razer's official August 5 2026 notes acknowledge repeated package downloads on Blade 16 2025, but do not name this missing DLL or establish either reporter's root cause. Razer distinguishes laptop THX, headset THX and standalone VAD products; the standalone app is not a substitute for the laptop stack.

## Watch out
OpenBlade currently composes only the current user's LocalAppData Razer/RazerAppEngine/User Data/Apps/Common/thxv4common path. GetPinnedFileStatus reports Missing only for FileNotFoundException or DirectoryNotFoundException; unreadable/hash-changed components have a different status. The missing-client error occurs before LoadLibrary and before later DriverStore/configuration/service verification, so it does not establish a secondary native dependency failure or a healthy remaining stack. A manifest destination name is not proof of a reporter's resolved absolute path. Keep alternate locations, installation/uninstall behavior and reporter filesystem contents unconfirmed until observed. Do not copy findings from the local maintainer machine to a remote reporter.
