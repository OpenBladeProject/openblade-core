---
title: Keep Linux and macOS HID discovery separate from admission
author: Jack
date: 2026-11-09
branch: feat/portable-hid-discovery
touches:
  - ".github/workflows/rust.yml"
  - build/PublishPayloadValidation.ps1
  - rust/README.md
  - tests/Build.Tests/PublishPayloadValidation.Tests.ps1
  - rust/tools/openblade-discovery/.gitignore
  - rust/tools/openblade-discovery/Cargo.lock
  - rust/tools/openblade-discovery/Cargo.toml
  - rust/tools/openblade-discovery/README.md
  - rust/tools/openblade-discovery/fixtures/linux-ambiguous.json
  - rust/tools/openblade-discovery/fixtures/linux-incomplete.json
---

## Decided
The developer discovery pilot uses a separate Cargo workspace under rust/tools/openblade-discovery. Linux reads bounded DMI and HID modalias metadata; macOS uses hw.model and copies selected IOHIDDevice registry properties. Snapshot v1 retains only coarse host metadata, sorted Razer USB PID counts, and explicit enumeration states. exactModel is always null.

## Tried and ruled out
Do not read Linux HID uevent: it includes HID_UNIQ. The read-only modalias attribute carries the needed bus/VID/PID. Do not copy the Windows C# model catalog or use USB IDs or macOS model strings to infer the Blade's exact model. No IOServiceOpen, IOHIDManager input subscriptions, device-node opens, or report operations are needed.

## Watch out
Several entries with one PID can describe one physical unit; an external device can have a candidate PID. An incomplete scan is not evidence of absence. Intel macOS can present Apple SMBIOS identity. Hosted tests verify metadata code paths only; a known physical Blade and a separate authority boundary are needed for any future control work.
