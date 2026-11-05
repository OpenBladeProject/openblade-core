---
title: Pin the Microsoft VC Redistributable content URL for Nightly
author: Jack
date: 2026-11-05
branch: fix/nightly-vcredist-download
touches:
  - build/InstallerPrerequisites.ps1
---

## Decided

Acquire VC Redist x64 14.50.35719.0 from the content-addressed Microsoft CDN URL. Keep the existing expected SHA-256 and valid Microsoft Authenticode requirement. The downloaded bytes match the previous pin exactly.

## Tried and ruled out

The hosted Nightly runner got a different SHA-256 from Microsoft's versioned aka.ms alias while the maintainer host got the pinned artifact from the same alias. Neither substituting the unexpected digest nor using the moving latest alias preserves the reviewed payload.

## Watch out

Use the actual prerequisite staging path to verify the Microsoft URL, hash and signature without running the executable. After the fix merges, dispatch a new hosted Nightly because reruns retain the original workflow source SHA.
