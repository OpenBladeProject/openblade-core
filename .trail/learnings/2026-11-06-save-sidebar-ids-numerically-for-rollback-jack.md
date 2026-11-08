---
title: Save sidebar IDs numerically for rollback compatibility
author: Jack
date: 2026-11-06
branch: feat/labs-sidebar
touches:
  - src/OpenBlade.Tray.Core/UiPreferenceStore.cs
  - tests/OpenBlade.Tray.Tests/UiPreferenceStoreTests.cs
---

## Decided
Write primary navigation entries as their stable integer IDs in UiPreferenceStore. Continue reading string names and numbers, including draft builds that saved "Labs", and leave unrelated enum serialization alone.

## Tried and ruled out
The pre-Labs enum with the existing JsonStringEnumConverter rejects "Labs" with JsonException. The older loader falls back to default preferences for the whole file. The same reader accepts integer 7, and the old navigation normalization skips the undefined value while retaining the other entries.

## Watch out
Keep the existing enum numbers fixed. Test old named and numeric input, the saved numeric JSON, and preservation of the custom order and an unrelated preference. Both normal saves and future-dashboard-preserving saves use the shared serializer options.
