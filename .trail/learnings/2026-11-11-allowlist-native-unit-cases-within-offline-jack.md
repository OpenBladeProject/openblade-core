---
title: Allowlist native unit cases within offline no-probe authority
author: Jack
date: 2026-11-11
branch: fix/native-recovery-lifetime
touches:
  - tools/openblade-windows-host/src/windows/scm/installed.rs
---

## Decided
Under an offline no-probe scope, run explicit injected/pure unit-test modules rather than an installed:: substring. List the selected harness cases before execution and refuse any private_pipe or other native I/O probe name. Unit harness binaries are distinct from product role entry points, but their tests can still perform forbidden native operations.

## Tried and ruled out
The recovery/lifetime session's intermediate 09552667 run selected installed:: and unintentionally executed two private named-pipe cases, private_pipe_checks_same_handle_and_retires_canceled_accept and private_pipe_serves_and_retires_observed_v79_status. These perform private NPFS and self peer/token/session observations, outside the requested no-pipe/probe authority. The parent disclosed the selection mistake, retained the exact intermediate log, and did not repeat those cases. No product or fixture role was launched. Do not describe this session as entirely no-probe or use those cases as final authorized evidence.

## Watch out
The new lost-SCM-stop-sender regression also caught early startup returns bypassing the terminal lost-channel check. Classify loss around the entire lifecycle body before run_lifecycle normalizes typed startup cancellation, preserving real trust/cleanup errors. Final verification must identify the subsequent actual source head/tree and distinguish its passing allowlisted cases from failed/intermediate and out-of-scope evidence. This addendum corrects the preceding recovery learning's no-probe statement for this session's later execution history; it changes no product or hardware authority.
