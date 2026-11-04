---
title: Read atomically published fixture reports through rename completion
author: Jack
date: 2026-11-03
branch: fix/codebase-reliability-audit
touches:
  - tests/OpenBlade.DeploymentClient.Tests/IndependentInstalledProcessTests.cs
---

## Decided

Independent launcher fixture reports are fully written to a temporary file and atomically renamed. Readers must open the published destination with FileShare.Read | FileShare.Delete: the destination can become visible while the rename operation still holds DELETE access. Apply the shared test-only reader to parent.ready, report.json and replaceable coordinator-phase diagnostics. Continue denying content writes.

## Tried and ruled out

The full solution run at d60f4d46 failed with a sharing IOException while reading report.json. The producer already closed its content writer before rename, so adding another atomic writer was unnecessary. A deterministic test holds a read handle with DELETE access through FileOptions.DeleteOnClose; the old File.ReadAllText reader fails and the corrected reader succeeds on the complete content. The separate CI bootstrap reader waits for its retained producer to terminate and does not have this live-publication race.

## Watch out

Do not solve report publication races with sleeps, larger production timeouts, permissive content-write sharing or retries that conceal malformed JSON. Preserve launcher child/job/identity assertions and process cleanup. This is a fixture synchronization correction, not a production launch behavior change. Keep the original failing run and verify the focused suite plus complete solution after committing.
