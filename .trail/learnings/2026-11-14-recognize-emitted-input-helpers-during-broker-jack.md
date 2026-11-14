---
title: Recognize emitted input helpers during broker relaunch
author: Jack
date: 2026-11-14
branch: fix/broker-relaunch-check
touches:
  - src/OpenBlade.DeploymentClient/InstalledProcessFamilyPolicy.cs
  - src/OpenBlade.DeploymentClient/DeploymentClientCoordinator.cs
  - src/OpenBlade.SessionAgent/InputHelperCommandLine.cs
---

The installed Agent can spawn a five-token Razer-driver input supervisor during its first second. Legacy-only inventory parsing classified that legitimate family as ambiguous and aborted deployment before Tray launch. Share the pure argument grammar with the Agent producer/parser, bind workers to the supervisor requested feature set, and allow Windows-hook SnapTap alongside Razer-driver FunctionRow while rejecting overlapping SnapTap ownership. Bound startup inventory settling while retaining original child liveness, fresh caller context, exact path/SID/session/integrity/creation/parent checks, final consecutive observations, and no installer replay. Updating the fixed deployment client requires documented attended broker maintenance; an app-only broker install does not replace it.
