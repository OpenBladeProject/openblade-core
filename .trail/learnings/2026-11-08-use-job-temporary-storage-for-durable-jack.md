---
title: Use job temporary storage for durable guardian CI tests
author: Jack
date: 2026-11-08
branch: feat/battery-saver-low-fan-validation
touches:
  - ".github/workflows/build.yml"
---

## Decided
Run quality-lane tests with TEMP and TMP under GitHub's RUNNER_TEMP/openblade-tests. Measure 32 write-through, flush-to-disk records in both the original temp root and job temp root before testing. Keep the production journal, its durability, all safety deadlines, and test assertions unchanged.

## Tried and ruled out
PR #879 run 42459395640 passed every test project except Capture, where 25 guardian tests exceeded 15/20/30-second outer waits. Capture took 23m50s. Journal sharing exceptions occurred during teardown while owners were still running and do not identify the original failure. The focused Debug tests passed locally with DOTNET_PROCESSOR_COUNT=2 (46 tests in 28.8s), so low CPU count alone did not reproduce the problem. Do not skip guardian coverage, disable disk flushes, or raise production safety limits to obtain green CI.

## Watch out
Hosted validation of the new storage path is still required; the initial evidence suggests storage latency but does not prove it. RUNNER_TEMP may share the same volume on some images. Use the printed timings and exact-head test outcome before claiming a root cause or resolution. This changes CI only and does not require repeating the completed physical Stop/Auto trial.
