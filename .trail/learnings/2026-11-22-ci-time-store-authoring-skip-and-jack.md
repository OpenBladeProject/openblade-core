---
title: "CI time: Store authoring skip and full-validation sharding"
author: Jack
date: 2026-11-22
branch: chore/skip-ci-store-authoring
cost: { minutes: 16, tokens: 248067 }
touches:
  - ".github/workflows/build.yml"
  - build/Publish.ps1
  - docs/microsoft-store-submission.md
  - tests/Build.Tests/LocalizationTranslatorWorkflow.Tests.ps1
  - tests/Build.Tests/NightlyWorkflow.Tests.ps1
---

## Decided
- The 38-40 min "Validate installer and Store authoring" step was almost entirely LocalizationTranslatorWorkflow.Tests.ps1 (34 min: 10 min of workbook checks, then three package exports of about 8 min each). Store tests take 0.5 s and the Store readiness report about 6 s.
- Publish.ps1 -SkipStoreReadiness is used only by the Stable CI installer lane. Nightly and release publication keep the report.
- Full mode shards contracts into general, translator-same-process and translator-fresh-process (the translator suite takes -Part), and quality into format, solution and capture. The solution shard filters with FullyQualifiedName!~OpenBlade.Capture.Tests.
- Capture.Tests alone takes 10m48s of the roughly 21-min solution test step.

## Tried and ruled out
- Reindenting the translator workbook section: it contains here-strings, so the short package section was moved ahead of it instead.
- Running package exports concurrently on one runner: snapshot capture depends on the foreground window, so concurrent runs would interfere.

## Watch out
- A VSTest filter that matches nothing in an assembly exits 0 (checked locally).
- NightlyWorkflow.Tests requires every Capture test source to use the OpenBlade.Capture.Tests namespace, and no other project to use it; otherwise the filter would silently drop tests.
- Shared artifact names in a matrix must include matrix.shard, or upload-artifact v4 fails with 409.
- Only the build-and-test check is required, so renaming shard jobs is safe.
