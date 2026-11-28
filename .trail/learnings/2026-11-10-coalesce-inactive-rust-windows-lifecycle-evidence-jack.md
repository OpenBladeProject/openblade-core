---
title: Coalesce inactive Rust Windows lifecycle evidence
author: Jack
date: 2026-11-10
branch: feat/rust-windows-lifecycle
touches:
  - tools/openblade-windows-host/src/windows/scm/lifecycle.rs
  - tools/openblade-windows-host/src/windows/scm/lifecycle/native.rs
  - tools/openblade-windows-host/src/windows/scm/lifecycle/tests.rs
---

## Decided
Keep Windows SCM power and session lifecycle evidence in an inactive Windows-only compile-check module. Map suspend and resume at the callback boundary, coalesce a sticky invalidation and the latest refresh request, and serialize invalidation, observation and retirement through one worker. WTS event IDs are refresh hints. Only a fresh console query supplies a console ID, and a missing query or unknown AC result replaces old evidence.

## Tried and ruled out
A buffered event replay can replay historical suspend/resume work and grow during a callback storm. A cached console or event payload ID cannot establish current console authority. Windows AC status 1 does not establish adapter adequacy.

## Watch out
This worker emits observations. The native SCM callback context, power-setting subscriptions and unregister/drain ordering, live delivery, authenticated session policy and HID owner wiring still need separate validation. Give STOP priority over queued work and await retirement before terminal SCM status. Default SCM and the managed writer remain separate. Trail check can match Windows, power, session and callback vocabulary against unrelated learnings. Inspect the proposed path and confirm the cited behavior is absent before overriding a match.
