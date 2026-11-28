---
title: Authenticate retained mailbox preparations before renewal publication
author: Jack
date: 2026-11-12
branch: fix/mailbox-renewal-race
touches:
  - src/OpenBlade.Deployment/ManagedMailboxLegacyRecovery.cs
  - src/OpenBlade.Deployment/ManagedMailboxNativeCustody.cs
  - src/OpenBlade.Deployment/ManagedMailboxRecovery.cs
  - src/OpenBlade.Deployment/WindowsManagedMailboxHandoff.cs
  - src/OpenBlade.Deployment/WindowsNativeAttemptHistory.cs
  - src/OpenBlade.Deployment/WindowsNativeEpoch.cs
  - src/OpenBlade.Deployment/WindowsNativeInstalledSelection.cs
  - tests/OpenBlade.Deployment.Tests/ManagedMailboxCorrectionTests.cs
  - tests/OpenBlade.Deployment.Tests/OpenBladeSignedReleaseVerifierTests.cs
  - tests/OpenBlade.Deployment.Tests/WindowsNativeInventoryBurnExtractorTests.cs
---

## Decided

Authenticate every retained canceled mailbox preparation under fresh native writer, original attempt custody and owner custody before admitting a later managed run or native Begin. Bind the original run, exact request, signed retained managed cache, receipt, original-exit proof, intent, mapping, reservation and terminal proof. Reuse the matched signed native generation's protected enrollment projection for historical cache authentication; current enrollment may have rotated. Global safety is checked after the complete historical chain, so an earlier unreserved cancellation does not block authentication of a later preparation.

Begin acquires and revalidates the exact active handoff exclusion before publishing any preparation and retains it through intent/mapping publication. An atomic preparation envelope contains the provisional origin and exact transfer mapping, allowing authenticated completion after a crash on either side of origin publication. Repairs follow original/cache/proof authentication and do not submit native startup or firmware work. Completed terminal history requires the already-published origin and mappings.

Setup's pre-stop selection authenticates complete immutable current and legacy terminals under metadata writers without reacquiring a new managed run's lifetime-held exclusions. Active recovery still requires exclusion. Begin uses the same native writer throughout and retains legacy-before-current exclusion ordering.

## Tried and ruled out

The C2/C6 ACL findings were invalid; retain original owner/DACL rules and TrustedInstaller handling. C4 missing original-exit/receipt remains fenced. C5 was invalid as reported: resume retires uncertain ports without writes, poisoned sessions cannot reacquire, and final shutdown has explicit guarded release. A possible active-gate/suspend maintenance window does not justify clearing uncertainty or replaying a write.

Publishing a mapping before origin only moves the crash gap; the full positive preparation binding must be atomic. Authenticating only the latest transfer or reusing an in-memory canceled set does not support fresh custody. A terminal string alone, absent process/lock, fake Retired, reservation deletion or history reset is never authority.

## Watch out

A retained legacy exclusion stays with the next managed startup until exit; Setup selection runs before stop. Do not reacquire that exclusion in pre-stop terminal-only authentication. Never repair partial publication before authenticating the original signed transfer. Keep unknown, malformed, active, submitted, unmatched reservation and ambiguous histories fenced. Test no-reservation predecessor followed by canceled preparation, atomic preparation/origin/intent/mapping/terminal cuts, startup keeping exclusion, actual Setup cleanup-await cancellation, and the next real Begin. Offline injected/private-filesystem and static-PE results are not runtime, installation or hardware proof. Rust source evidence may be reused only with exact input/harness hash bindings.
