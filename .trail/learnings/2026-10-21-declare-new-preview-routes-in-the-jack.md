---
title: Declare new preview routes in the translator screenshot map
author: Jack
date: 2026-10-21
branch: feat/app-profiles-telemetry-battery
touches:
  - build/Capture-LocalizationSnapshots.ps1
  - build/localization/TranslatorScreenshotMap.json
  - tests/Build.Tests/LocalizationTranslatorWorkflow.Tests.ps1
---

## Decided
New localization preview routes must be declared in build/localization/TranslatorScreenshotMap.json as well as Capture-LocalizationSnapshots.ps1. Put feature-specific first-match rules before broader rules and map both Tray resource IDs and Tray.Core source keys. The application profiles, telemetry, battery history and brightness routes cover eight images and 137 newly added keys; the translator workflow asserts representative rows for both catalogs.

## Tried and ruled out
Removing the new captures would hide missing translator context. A successful resource validator or standalone render does not exercise screenshot inventory validation or deterministic translator archives. Whitespace-only formatting also misses the IMPORTS check used by CI; run the exact full dotnet format verification before pushing new UI files.

## Watch out
Use Windows PowerShell for tests/Build.Tests scripts, run LocalizationTranslatorWorkflow.Tests.ps1 with its real renders and archive checks, and keep render/build operations serial within one worktree. Metadata-only screenshot-map changes do not change the installed product payload or its validated hardware behavior.
