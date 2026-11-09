---
title: Retry transient PawnIO asset acquisition without weakening pins
author: Jack
date: 2026-11-08
branch: fix/nightly-pawnio-download-retries
touches:
  - build/PawnIoPrerequisite.ps1
  - tests/Build.Tests/PawnIoPrerequisite.Tests.ps1
---

## Decided
PawnIO build acquisition retries only HTTP 408, 429, 500, 502, 503 and 504 or a WebException timeout. Use at most four requests, each with the existing 60-second timeout, separated by 5, 15 and 30 seconds. Keep the exact official asset API, versioned binary headers, pinned digest and publisher unchanged. Delete partial download bytes before retrying. Verify integrity once after acquisition and before atomic cache admission; trust or permanent acquisition failures are not retried.

## Tried and ruled out
Nightly 108 completed all 16,189 tests and build contracts but three attempts failed at the official asset API with HTTP 503. Local full downloads also alternated between failures and exact-byte success, so the evidence did not establish a PowerShell-specific defect. The patched real acquisition encountered three 503 responses and then succeeded on the fourth request with the pinned 3,410,960-byte signed installer. Mocked PowerShell 5.1 and 7 tests cover recovery, exhaustion, permanent failure, temporary-file cleanup, cache reuse and no integrity retry.

## Watch out
Retry is bounded resilience, not proof the upstream service cannot remain unavailable. Never retry a bad hash or publisher, execute the installer during acquisition, substitute another artifact, or treat a metadata response as installer content. The local Nightly fallback is not eligible merely because a source-controlled hosted acquisition step fails. An independent review found no blocking issues; the PowerShell 7 HTTP exception response shape was separately inspected.
