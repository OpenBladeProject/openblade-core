---
title: "Native RZ09-0581 lighting slice L3: frame stream, steps, refresh"
author: Jack
date: 2026-11-27
branch: feat/native-lighting-controls
---

## Decided
- Native RZ09-0581 slice L3 streams animated lighting from a dedicated task (`Runtime::run_lighting_stream`, a pinned future in `serve_connections`), not the 500 ms origin tick or mailbox jobs. Frames go through `MatrixStream`; on Windows a thread owns its own matrix lease, and `MATRIX_IO` serializes it with job matrix writes.
- Each frame try-locks `Runtime::gate` (held means skip one interval) and checks the low-power boundary, the doubt latch, a clean reading no older than 1 s (`CLEAN_READING_AGE`) and `Fresh::authorize_frame` (every write check except the controller inventory) at the same power generation.
- The origin tick supplies the clean reading. `confirm_stream_clean` needs both capture and `fresh.authorize()`; a typed competing-controller capture error retires the renderer.
- A render job draws its first frame before any setter, so a failed `Random` refuses it with nothing sent.
- `stepKeyboardBrightness` and `setLightingRefreshRate` are served only with service-profiles-v1 (`lighting_controls.rs`). Desired lighting is the override, else the profile layer at the job's power class. An applied `setLightingOverride` saves the override as `LastKnownLighting`.

## Tried and ruled out
- A clean reading from `capture` alone: the fake's `fresh_refuse` (and WindowsGuard's per-write refusals) only show up in `authorize`, so stale admission kept streaming.
- Comparing raw matrix reports in tests: byte 2 is a global transaction counter. Decode them with `decode_feature_report` first.

## Watch out
- Managed `PercentToRaw` rounds up (6% is 0x10); `RawToPercent` rounds to nearest.
- `manual-overrides.json` holds only persistent overrides, so read a temporary one from status `lightingOverride`.
- The Bash tool moves any command running longer than 180 s to the background. Do not restart a lane runner without confirming the old one exited: TaskStop left the first `lanes.sh` running and both wrote one summary.
- On the pinned toolchain clippy flags `chunks_exact(N)`; use `as_chunks::<N>()`.
