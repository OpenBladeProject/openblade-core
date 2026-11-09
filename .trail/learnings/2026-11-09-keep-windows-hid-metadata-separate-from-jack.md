---
title: Keep Windows HID metadata separate from handle identity
author: Jack
date: 2026-11-09
branch: feat/rust-windows-discovery
touches:
  - tools/openblade-windows-host/src/discovery/mod.rs
  - tools/openblade-windows-host/src/discovery/windows.rs
  - tests/OpenBlade.Protocol.Tests/WindowsRustModelEvidenceInventoryTests.cs
---

## Decided
Phase 3a uses the managed model-evidence inventory for exact Windows registry names and optional control usage selectors. The two physically validated models pin usage 0001:0002. The shared source-reviewed selector pins model VID/PID and 91-byte feature length but has no fixed usage pair. Windows HIDClass hardware IDs are only pre-open candidate evidence.

## Tried and ruled out
Parsing an interface symbolic path or deriving a registry label from the catalog display name can misclassify the device. SetupAPI supplies devnode hardware IDs and an opaque interface name for duplicate detection. SKU and BIOS are diagnostics on the model-scoped path.

## Watch out
Phase 3b must independently read handle attributes and caps, preserve controller and per-feature admission, and retire ambiguous or cancelled overlapped operations only after terminal completion. A single metadata candidate grants no session or write authority.
