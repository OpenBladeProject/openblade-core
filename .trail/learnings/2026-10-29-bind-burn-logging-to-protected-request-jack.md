---
title: Bind Burn logging to protected request staging explicitly
author: Jack
date: 2026-10-29
branch: feat/tray-icon-redesign
touches:
  - AGENTS.md
  - docs/unattended-deployment-broker.md
  - src/OpenBlade.DeploymentBroker/DeploymentPolicy.cs
  - tests/OpenBlade.DeploymentBroker.Tests/ApplyFixedInboxBrokerRequestHandlerTests.cs
  - tests/OpenBlade.DeploymentBroker.Tests/DeploymentPolicyTests.cs
---

## Decided
Bind Burn logging explicitly to the broker-derived OpenBlade-Install.log file in the protected request staging directory. Append the fixed /log switch through ProcessStartInfo.ArgumentList and reject a preexisting path. Burn derives the existing Setup package-log argument from the same prefix. Preserve the fixed install/quiet/norestart operation and do not accept caller-supplied log paths.

## Tried and ruled out
A successful real request with redirected TEMP/TMP produced an empty retained-log index. Redirecting temporary environment variables alone does not establish where Burn creates logs. Do not claim that archiving existing .log files guarantees log production, or infer the prior installer's failure cause from a later successful retry.

## Watch out
Use installer-logs as the actual protected archive directory name. An unchanged product installer can remain installed while developer-only broker logging is corrected. Validate the new launch arguments and archive behavior without repeating an unrelated hardware test or forcing an installation failure.
