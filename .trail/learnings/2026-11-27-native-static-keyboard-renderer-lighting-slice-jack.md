---
title: Native Static keyboard renderer (lighting slice L2)
author: Jack
date: 2026-11-27
branch: feat/native-lighting-matrix
---

## Decided
- Native Static renderer (slice L2) lives in `performance/lighting/render.rs`; the runtime state (doubt latch, session layout byte, owned renderer and its keepalive due) is `lighting_mutation::Session` on `Runtime::lighting`.
- The keepalive runs on the origin tick after `process_power_source` and before the Driver-mode step (`StatusSource::maintain_lighting`), every 5 s on the first frame's grid (managed `AdvanceFrameDeadline`). A frame refused before submission keeps the renderer; a submitted one that fails retires it and latches the doubt.
- A retained renderer (same color, not waking from dark) leaves `MatrixState::Kept`, so its cadence is unchanged; only a new frame resets `due`.
- An Off write retires the renderer unless it stopped before a setter or restored its confirmed baseline (`retires_renderer`).
- Stop and the low-power boundary retire the renderer without a write; managed's low-power Off and resume restore are still not ported.
- Native sends no 00/04 for lighting; the Driver-mode owner keeps the mode, and the handshake's 00/84 read admits Normal or Driver.

## Tried and ruled out
- Setting `MatrixState::Owned` on a retained renderer reset the keepalive grid on every same-color request.

## Watch out
- Each lighting exchange passes about four admission checkpoints, so a `refuse_from` test has to sweep a range instead of naming one count.
- The matrix fixture counts only submitted frames: a refused frame does not take a number, so `matrix_refuse` and `matrix_fail` can both be `Some(1)` to fail the restore frame after a refused first one.
- Runtime cadence tests move the clock with pause, advance and resume between ticks, and wrap each tick in `Rig::held(HeldClock::Keep, ...)`. Never sleep on the real clock.
- The native fixture scope needs PowerShell 7 and Python 3.11 or newer; neither is on this host.
