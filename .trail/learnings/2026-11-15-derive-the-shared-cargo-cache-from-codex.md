---
title: Derive the shared Cargo cache from the Git repository name
author: Codex
date: 2026-11-15
branch: fix/pawnio-install-diagnostics
touches:
  - build/NativePayload.ps1
  - tests/Build.Tests/NativePayload.Tests.ps1
---

## Decided
The inherited native build helper derives its shared-cache key from the main checkout directory name, overriding CARGO_TARGET_DIR. In the historical OpenSynapse checkout this selects C:/build-cache/cargo/OpenSynapse instead of the repository's required C:/build-cache/cargo/openblade-core. Prefer the basename of Git origin for both target directory and publication mutex; preserve the old common-directory fallback only for an origin-less repository, and reject a failed metadata read instead of guessing another cache.
## Tried and ruled out
A temporary Git common-directory junction does not change git rev-parse's canonical common path and therefore cannot correct this identity. It was removed without touching its target. Do not bypass provenance, clone a repository, copy a toolchain, delete a pre-existing shared cache, or include the primary checkout's unfinished storage workflow just to work around the old key.
Scoped metadata-only fixtures cover HTTPS, SSH, trailing-slash URLs, origin absence and metadata failure. The actual helper and packaging-contract tests passed under Windows PowerShell5.1 and returned the required canonical path and mutex. PowerShell source inspected through serialized tool output doubles backslashes in presentation; actual character counts and direct execution establish correct string/regex literals.
## Watch out
Keep downloaded credentials and complete remote URLs out of logs. Derive one safe filename component; never let origin metadata supply an arbitrary cache directory. This build-only correction affects native development and publishing, not product hardware capabilities. The pre-existing legacy cache may belong to other active work; retain it rather than sweeping by name or age. Native release provenance includes NativePayload.ps1, so commit the correction and rebuild the native payload before final packaging validation.
