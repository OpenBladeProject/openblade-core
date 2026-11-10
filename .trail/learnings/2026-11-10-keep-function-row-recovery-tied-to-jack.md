---
title: Keep function row recovery tied to press ownership
author: Jack
date: 2026-11-10
branch: feat/rust-function-row-policy
touches:
  - rust/crates/openblade-domain/src/function_row.rs
  - rust/crates/openblade-domain/tests/function_row_policy.rs
---

## Decided
The Windows hook can reconcile a missed edge after selection changes only while it still tracks a key. A repeat observed without the original down stays on the original F-key route. An unknown backend observation denies media while an explicit handoff or stop starts retirement.

## Tried and ruled out
Do not infer a completed transfer from backend uncertainty or a successful reset after a processor fault. Do not retry a possibly submitted forward or replay a failed release during cleanup.

## Watch out
Attempt the other independent media releases before resetting when one release fails. The host owns the retirement deadline and must stop reading the retired subscription before separately verifying hook restoration.
