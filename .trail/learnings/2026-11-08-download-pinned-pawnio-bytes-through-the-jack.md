---
title: Download pinned PawnIO bytes through the official asset API
author: Jack
date: 2026-11-08
branch: feat/battery-saver-low-fan-validation
touches:
  - build/PawnIoPrerequisite.ps1
  - build/third-party/PawnIO-NOTICE.txt
  - tests/Build.Tests/PawnIoPrerequisite.Tests.ps1
---

## Decided
Builds acquire the pinned PawnIO 2.2.0 installer through the official GitHub release asset API at asset ID 374375077, requesting binary content with the versioned API header. Keep the existing SHA-256, Authenticode publisher, cache admission, and final payload verification. The browser release URL remains source provenance in the notice. Bound acquisition to 60 seconds and use basic parsing for Windows PowerShell 5.1.

## Tried and ruled out
Two hosted installer jobs and a local HEAD request received HTTP 504 from the browser release download URL. The release API still listed the exact asset, size 3410960, and the existing pinned digest. Anonymous binary API acquisition succeeded, and the actual build downloader then passed a fresh-cache Windows PowerShell 5.1 download with the same hash and valid original publisher signature. No package substitution, alternate vendor, token requirement, installation, or verification bypass was needed.

## Watch out
The API's default response is metadata, so the binary Accept header is required. Any unexpected metadata or changed bytes must fail the existing hash/signature checks. A successful download does not authorize executing the installer or changing the installed driver.
