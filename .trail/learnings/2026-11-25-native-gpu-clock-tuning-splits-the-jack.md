---
title: Native GPU clock tuning splits the paired write around the firmware readback
author: Jack
date: 2026-11-25
branch: feat/native-gpu-thermal-integration
---

## Decided
- Native GPU clock tuning (service-gpu-clock-tuning-v1, exact RZ09-0581) splits managed's paired write in two: thermal_gpu::Lane::prepare runs inside RequestPermit::resolve on the HID job (after intent is saved and every firmware refusal), and Lane::complete runs in apply_locked only after the independent firmware readback and a fresh `authorize() == Some(proof)` recheck. Managed submits NVML before its final read; native submits after it.
- Resume invalidation and power-change rearm are lazy: each step carries a Boundary (gate clear generation, Windows power generation) and the lane compares it with what it last saw.
- The pending block is the management machine's own SubmissionUncertain after a read-only resolution attempt, not managed's pendingWriteConfirmations tracker.
- Reconciliation paths (triggers, convergence, fan handback, drift) still refuse a GPU effect; setGpuClockTuning and the status projection are not served yet.
- Both package readers refuse the feature and marker by name until a WinVerifyTrust/catalog gate checks the loaded nvml.dll path.

## Tried and ruled out
- Computing the GPU Boundary for every permit: it reads the low-power gate once more and breaks the request_triggers low_power tests that count gate reads. Read it only when a lane is composed and the request is not a reconcile.
- Using Fake `queries_fail` to model an unreadable driver after a submission: the controller's own pre-write query fails first (NotApplied). Use the `lose_on_set` knob.

## Watch out
- The admission recheck before the NVML submission is only reachable by refusing the Nth authorize; the test searches `read_refuse_at` 1..=200 for the point where firmware landed and the submission was refused. A change in admission count moves the index but not the test.
- Git Bash `sed -i` on a CRLF doc rewrites the whole file to LF; normalize with perl afterwards.
- Worker::call_blocking and Storage::gpu_tuning are feature-gated; without the gate the non-GPU lanes fail clippy with dead code.
