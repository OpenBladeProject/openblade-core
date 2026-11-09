---
title: Retain deployment logs and classify Session Agent process families
author: Jack
date: 2026-10-29
branch: feat/tray-icon-redesign
touches:
  - AGENTS.md
  - CHANGELOG.md
  - docs/unattended-deployment-broker.md
  - src/OpenBlade.DeploymentBroker/ApplyFixedInboxBrokerRequestHandler.cs
  - src/OpenBlade.DeploymentBroker/BrokerEnrollment.cs
  - src/OpenBlade.DeploymentBroker/BrokerStartupReconciler.cs
  - src/OpenBlade.DeploymentClient/InstalledRelaunch.cs
  - tests/OpenBlade.DeploymentBroker.Tests/ApplyFixedInboxBrokerRequestHandlerTests.cs
  - tests/OpenBlade.DeploymentBroker.Tests/BrokerStartupReconcilerTests.cs
  - tests/OpenBlade.DeploymentClient.Tests/IndependentInstalledProcessTests.cs
---

## Decided
Retain bounded protected Burn and Setup .log snapshots before deleting completed deployment staging, including startup reconciliation. Keep log-retention status separate from the verified install/rollback outcome. If retention fails, preserve source staging and the reconciliation latch rather than erasing the only evidence. Keep exact broker response codes because the client uses install-rolled-back to authorize restoration of the previous process shape.

Count the primary Session Agent separately from its known SnapTap and THX helper family. Verify exact installed image, role arguments, actual parent chain, process creation order, owner, session and normal integrity. Helpers remain counted for shutdown emptiness; unknown, orphaned and duplicate roles fail closed. Admit the Tray's documented --startup argument as well as no-argument activation.

## Tried and ruled out
Do not treat a post-rollback helper-count error as the original installer failure. Do not accept arbitrary extra same-name processes or merely ignore helper executables during shutdown. Do not delete request staging before durable diagnostic retention. No device-model restriction belongs on these shared developer deployment paths.

## Watch out
Updating already enrolled broker/client binaries requires the documented attended uninstall/install workflow, not an ordinary app update. Preserve existing protected reports before that workflow removes the old enrollment directory. Reuse an unchanged validated installer only after confirming its exact hash and product source; re-sign a fresh manifest for the new enrollment identity. Installation and provisioning still need explicit authorization.
