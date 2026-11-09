---
title: Distinguish empty IOKit results and malformed discovery metadata
author: Jack
date: 2026-11-09
branch: feat/portable-hid-discovery
touches:
  - tools/openblade-discovery/Cargo.toml
  - tools/openblade-discovery/README.md
  - tools/openblade-discovery/src/linux.rs
  - tools/openblade-discovery/src/macos.rs
  - tools/openblade-discovery/src/main.rs
  - tools/openblade-discovery/src/snapshot.rs
  - tools/openblade-discovery/src/snapshot/tests.rs
---

## Decided
On macOS, a successful null iterator means no matching services per IOKitLib.h. For published vendor metadata, compare the integer before any USB-width conversion. Require a lossless USB-width product only for an exact Razer vendor on USB. Keep the developer crate lint policy separate from the production workspace.

## Tried and ruled out
The read-only advisory review flagged the successful empty iterator and proposed extending the production Cargo guard or adding new system crates. The documented success contract and scope of this pilot do not require those changes. Both Mac runner architectures exercise native metadata and the integer conversion test.

## Watch out
Linux DMI vendor and product labels are evidence only; case normalization and well-formed Unicode must not introduce model admission. Exact model remains unknown even when one Razer HID entry is present.
