---
title: Keep SnapTap setup visible and install reviewed drivers through Windows Update first
author: Jack
date: 2026-10-26
branch: feat/snaptap-windows-update-setup
touches:
  - AGENTS.md
  - Directory.Build.props
  - docs/snap-tap.md
  - src/OpenBlade.SessionAgent/RazerSnapTapDriverAdmission.cs
  - src/OpenBlade.Tray.Core/OpenBlade.Tray.Core.csproj
  - src/OpenBlade.Tray.Core/RazerDriverInstallJournal.cs
  - src/OpenBlade.Tray.Core/RazerKeyboardDriverInstaller.WindowsUpdate.cs
  - src/OpenBlade.Tray.Core/RazerKeyboardDriverInstaller.cs
  - src/OpenBlade.Tray.Core/RazerKeyboardWindowsUpdateJob.cs
  - src/OpenBlade.Tray.Core/RazerKeyboardWindowsUpdateNativeSession.cs
---

## Decided
Keep SnapTap Install driver and Setup guide visible for runtime Unavailable as well as DriverMissing. A refused preflight is a blocked action, not a reason to hide setup. Preserve retry for unsuccessful pre-submission outcomes and guide-only restart/recovery behavior. Do not offer driver repair for an ordinary input-loop failure.

Keyboard setup now searches automatic and optional Windows Update driver offers first for all seven reviewed exact-model recipes. Select by reviewed INF hardware IDs, provider and every present matching target, never title text. Retain actual searched COM objects and exact update IDs/revisions. Journal intent before fresh per-submission admission, preserve all per-update results, and independently verify bound catalogs/services before success. Only an initial successful NoOffers before any WU installation can fall back to unchanged pinned packages. Failed, partial, canceled or uncertain installation never switches sources.

A full-looking tree is not independent installation proof. Verify every currently bound Razer role even in a partial stack. Compatible installed updates are admitted through current package trust without source-hash whitelists. Genuinely missing images and the narrowly reviewed pinned-source inbox keyboard binding are repairable but not Installed. Unknown orphan images and catalog failures remain refused. A fully observed dependency pass may continue only when all prior targets remain unchanged and a reviewed common or virtual child appears; cap the passes and never replay a submitted update ID.

## Tried and ruled out
Generic Unavailable dropped the exact setup blocker. Every host/session/power/controller/topology/service/image/trust refusal now retains a concrete stage/reason and bounded observed state. Source selection failures also retain the rejected update identity and metadata even before selection exists. The elevated helper previously returned before normal Tray logging initialization: give it its own exported persistent log and explicitly flush before Environment.Exit, which skips using/finally cleanup.

Do not scope enumeration by instance-ID substring alone: another ROOT/software node may expose a reviewed compatible hardware ID. Include every matching node, then reject foreign prefix/container/ancestry. Winner/focus recovery remains separate PR854. No update-policy changes, orchestrator disabling, service shutdown, license auto-acceptance, arbitrary source paths or unrelated update installs are introduced.

## Watch out
WUA callbacks use the Windows SDK IUnknown interfaces, not IDispatch. Bound waits request abort; they do not claim an unfinished native operation stopped. Retain at most one pending native job and release it after actual completion; late results log evidence without clearing recovery or accessing a disposed journal. WUA IsBusy is advisory; native submission can still refuse a concurrent operation. Setup journals preserve transaction uncertainty across restart. Offline fake-source/job/transaction tests and disconnected multilingual rendering do not prove native WUA behavior, package availability, installation, restart or physical SnapTap on any model. Those remain attended validation.
