---
title: Retain child handles and separate Auto drift from uncertain writes
author: Jack
date: 2026-11-06
branch: feat/battery-saver-low-fan-validation
touches:
  - docs/so6120-balanced-pwm-trial.md
---

## Decided
An attended SO6120 trial declined before setting writes when normal Automatic PWM changed during approval. Its valid journal accounted for 26 completed A0 reads and zero A1 publications, and native close succeeded. Preserve the strict captured-baseline-plus-four contract: another attempt must observe and approve a new plan, not retain much larger absolute targets after Auto drops to zero.

For Windows PowerShell 5.1 wrappers that inherit the console, keep the child creation handle with Process.Start(ProcessStartInfo) and UseShellExecute=false. Capture the actual child PID immediately, wait for that exact process, require its numeric exit code, then write the receipt. Keep wrapper and Capture identities separate and fail if either required receipt is missing.

## Tried and ruled out
On the exercised Windows PowerShell 5.1 host, Start-Process -NoNewWindow -PassThru returned a Process without a retained native handle. WaitForExit completed but ExitCode was null for harmless exit codes 0, 2 and 3. Direct Process.Start retained the handle and returned each actual code. An immediate Handle lookup also worked in the probe, but direct creation avoids a race to reopen an already-exited PID. Do not fabricate the historical missing exit code or count a wrapper receipt rejection as a hardware failure.

## Watch out
Unknown numeric exit status remains unknown. Completed typed transactions, an intact journal, native close and observed actual process exit can establish a no-setting-write outcome without inventing the exit code. Marker archival still needs explicit approval and exact evidence binding; no old journal is resumed, no marker is silently cleared, and the next trial retains fresh admission and its own approvals.
