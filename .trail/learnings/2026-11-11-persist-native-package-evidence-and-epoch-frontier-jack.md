---
title: Persist native package evidence and a replay-resistant epoch frontier
author: Jack
date: 2026-11-11
branch: feat/rust-package-installation-epoch
touches:
  - src/OpenBlade.Deployment/WindowsNativeEpoch.cs
  - src/OpenBlade.Deployment/WindowsNativePackage.cs
  - tools/openblade-windows-host/src/native_package.rs
---

## Decided
Authenticate and extract both native roles from the same held signed LocalDev
package. Keep original signed evidence, compiled per-artifact identity and the
protected derived inventory distinct from phase/current selection. Publish an
append-only frontier before replacing current, so stale valid selections and
interrupted publications deny independently after Setup exits.

## Tried and ruled out
A direct package.bin write can poison later preparations after a partial write.
An unresolved recovery must reconcile exact existing phase/frontier bytes after
its own crash. Neither a marker nor committed package evidence supplies loaded
origin, native drain or HID ownership. Retain package guards with the actual
native owner fence through retirement or quarantine.

## Watch out
The supported trust domain remains LocalDev enrollment; Stable provisioning is
missing. Pure crypto/private-filesystem tests and compiled mains do not establish
installed/kernel/physical behavior. The next stage must integrate eager origin,
immutable launch layout and the concrete provider without another placeholder.
