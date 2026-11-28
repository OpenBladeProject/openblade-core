---
title: Preserve application reporter recency and process lifetime
author: Jack
date: 2026-11-10
branch: feat/rust-windows-phase-6p
touches:
  - rust/crates/openblade-application-reporter/src/lib.rs
  - src/OpenBlade.SessionAgent/ApplicationProfileReporter.cs
---

## Decided

Keep the application-context reporter as an isolated Rust policy leaf. Match normalized full executable paths using the managed ordinal case-insensitive comparison supplied by the host. A current foreground match wins. Background retention needs an independently bound process lifetime and profile match. Reactivation advances recency while the retained lifetime stays stable.

## Tried and ruled out

A matching path or reused PID cannot identify the old process. Keep the latest authoritative profile snapshot outside the lossy foreground/exit queue. Only Applied and Stored acknowledge a report. A retry sends the current selection with the same instance and a fresh sequence and request ID.

## Watch out

Prune dead retained handles before a renewal or reconnect. A null executable path explicitly clears a previous context. The policy accepts scripted time, path comparison, process evidence and transport outcomes. Native path and lifetime admission, cancellation of the actual send, and current-user transport wiring still need separate verification.
