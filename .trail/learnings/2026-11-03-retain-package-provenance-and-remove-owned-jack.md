---
title: Retain package provenance and remove owned readonly directories
author: Jack
date: 2026-11-03
branch: fix/codebase-reliability-audit
touches:
  - src/OpenBlade.Tray.Core/ProvisioningPackageDirectory.cs
  - src/OpenBlade.Tray.Core/RazerDriverInstallJournal.cs
  - tests/OpenBlade.Tray.Tests/ProvisioningPackageDirectoryTests.cs
  - tests/OpenBlade.Tray.Tests/RazerKeyboardDriverInstallerTests.cs
---

## Decided

Pre-intent cleanup clears only ReadOnly on freshly admitted regular files and directories, including the attempt root, before nonrecursive deletion. Keyboard baselines record the exact package destination for manual recovery provenance; this adds no resume or replay authority.

The shared directory lifetime requires an exclusive journal lock, a fresh random GUID and a protected administrator/SYSTEM root. Keyboard and THX callers hold distinct exclusive locks before allocating destinations. Preserve this contract when adding callers.

## Tried and ruled out

An isolated Windows directory marked ReadOnly refused deletion until that attribute was cleared. Focused tests cover nested directories and the attempt root. Keyboard journal intents contain relative INF names, so they do not replace explicit package-directory provenance.

No current caller bypasses the extraction uncertainty catch. Brightness edits come from the guarded/resetting UI path; profile automation uses its own typed profile path. A same-GUID create race requires another elevated writer outside the serialized production ownership contract. Existing confirmed software paths do not establish crash-time orphan or submitted-package reclamation authority.

## Watch out

Retain before any attempted durable intent append, even when failure may have occurred before a write; preserve ambiguous flushes. Fresh ACL, type and reparse admission stays at deletion. Do not trade it for cached inventory attributes. Successful installation is not a general authorization to remove recovery evidence.
