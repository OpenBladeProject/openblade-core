---
title: Require Claude advisory review for each Rust migration stage
author: Jack
date: 2026-11-08
branch: docs/rust-boundaries-parity
touches:
  - docs/rust-boundaries.md
---

## Decided
Every Rust migration stage requires a real Claude advisory review of its exact scope and recorded base/head. Codex validates each finding against the reviewed revision, fixes supported issues and verifies affected checks. Record the disposition and resulting head in the stage PR. This is a migration-specific process requirement, not a repository-wide review rule.

## Tried and ruled out
A Codex review does not substitute for the requested Claude review. Do not attribute an earlier review to a later fix commit or silently rerun a metered review after Claude starts.

## Watch out
Incomplete, quota-limited or authentication-blocked runs are not clean reviews. Preserve the tracked run directory and completion/start metadata; report exactly what Claude reviewed and what Codex verified afterward.
