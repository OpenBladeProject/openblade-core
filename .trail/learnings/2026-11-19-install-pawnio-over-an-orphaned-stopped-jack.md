---
title: Install PawnIO over an orphaned stopped-service residue
author: Jack
date: 2026-11-19
branch: fix/pawnio-orphaned-service-install
cost: { minutes: 6, tokens: 153673 }
touches:
  - CHANGELOG.md
  - Directory.Build.props
  - src/OpenBlade.Platform/PawnIoRuntimeAdmission.cs
  - tests/OpenBlade.Platform.Tests/PawnIoRuntimeAdmissionTests.cs
---

## Decided
Issue #969 reporter log proved the state: PawnIO service + signed 2.2.0.0 DriverStore package remain after the user's PawnIO uninstall, but Present=0; Registered=0 Root\PawnIO devices. Admission (PawnIoRuntimeAdmission.Inspect) now returns null (absent) only when the service is the expected demand-start kernel service, STOPPED, passes path/catalog checks, no device is located with CM_LOCATE_DEVNODE_NORMAL, and no Uninstall\PawnIO key exists. Setup then runs the pinned official installer; journal replay protection and exit-0 readback are unchanged. Registered or active service without a device is preserved for repair with a specific message. PR #978, version 0.65.1.

## Tried and ruled out
PR #970 (count only present devnodes) was correct but cannot help a machine with zero devnodes. Making the CPU-temperature prerequisite non-fatal for the whole install was not done; it is a product policy change.

## Watch out
The official 2.2.0 installer's behavior over an existing stopped service is unverified (closed source); a non-zero exit fails without retry. The reporter must retest. sed -i in Git Bash rewrites the worktree copy to LF; restore with git checkout after committing.
