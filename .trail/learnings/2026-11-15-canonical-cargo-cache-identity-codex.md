---
title: Canonical Cargo cache identity
author: Codex
date: 2026-11-15
branch: fix/pawnio-install-diagnostics
touches:
  - build/NativePayload.ps1
  - tests/Build.Tests/NativePayload.Tests.ps1
---

## Decided
Derive a shared native build-cache identity from the Git origin repository name, not from a historical or task-specific checkout directory. Use the same identity for target directory and publication mutex. Retain a common-directory fallback only for repositories without origin metadata; reject a failed metadata read and unsafe name components.
## Tried and ruled out
Metadata-only fixtures cover HTTPS, SSH, trailing-slash URLs, origin absence and config failure. The actual Windows PowerShell5.1 function and native packaging contracts pass. A Git directory junction still resolves to the original canonical directory and cannot correct the key. Moving the machine-specific diagnostic note to personal storage preserves it while keeping historical checkout branding out of tracked product-source scanning; the generic implementation decision belongs here.
## Watch out
Native release provenance includes the build script, so commit changes before packaging. Do not copy toolchains, create a new target per phase, replace the primary checkout's pending workflow, print remote credentials, or delete pre-existing shared cache data to resolve an identity mismatch. Serialized tool output escapes backslashes; verify actual source characters and execution before diagnosing PowerShell literals.
