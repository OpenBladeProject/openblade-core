---
title: Include model coverage build guards and translations in support reviews
author: Jack
date: 2026-11-12
branch: feat/laptop-support-949
touches:
  - docs/experimental-blade-support.md
  - docs/fan-stop-model-coverage.md
  - docs/ru-RU/experimental-blade-support.md
  - docs/zh-CN/experimental-blade-support.md
  - evidence/source-reviewed-blades/2026-11-12-issue-949-models.json
  - tests/Build.Tests/ReadmeHardwareSupport.Tests.ps1
  - tests/OpenBlade.Protocol.Tests/SourceReviewedBladeModelTests.EarlyModels.cs
  - tests/OpenBlade.Protocol.Tests/SourceReviewedBladeModelTests.LegacyModels.cs
  - tests/OpenBlade.Protocol.Tests/SourceReviewedBladeModelTests.cs
  - tests/OpenBlade.SessionAgent.Tests/LegacyThxAudioCommandTests.cs
---

## Decided
After the issue949/2019Pro17 Claude review, model expansion must include the PowerShell README hardware-support build contract, not only C# catalog tests. Register each new model and HID identity there and update its expected row count. Keep EN/Chinese/Russian support guides aligned and record new models explicitly as NotEstablished in the FanStop coverage table when no exactEC Stop contract was reviewed.

## Tried and ruled out
A missing source excerpt can be an audit gap even when a complete own-model source hash already pins correct values: add short exact byte excerpts instead of borrowing sibling evidence. DisplayName need not be a unique identity: About renders DisplayName plus ModelNumber, catalog lookup uses ModelNumber, and fixture identities include model number and PID. Do not redesign these keys for a hypothetical future name-only lookup.

## Watch out
Assert each thermal command evidence pin is present in its model's source list. Keep model-role test membership in one helper and cover all legacy Advanced profiles in ambiguity/no-replay tests. The full local suite initially had seven unchanged timing failures; all affected cases passed isolated reruns. Preserve that distinction instead of calling the original full run green.
