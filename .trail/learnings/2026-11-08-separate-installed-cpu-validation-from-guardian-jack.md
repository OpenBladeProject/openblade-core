---
title: Separate installed CPU validation from guardian thermal admission
author: Jack
date: 2026-11-08
branch: feat/battery-saver-low-fan-validation
touches:
  - CHANGELOG.md
  - docs/so6120-cpu-temperature-source.md
  - docs/so6120-stop-auto-guardian.md
---

## Decided
Keep installed CPU-provider validation separate from a successful fan Stop/Auto round trip. The enrolled broker installed product source220097f9 with all372 payload files matching. Fresh service CPU observations changed normally. After service samples50/51/50C, the guardian independently sampled62C in172.9ms; its unchanged Auto ceiling55C correctly refused entry. GPU inactivity was confirmed. The refusal reported zero resolved EC commands, no mutation/pending command and released inactive ownership; fans remained Automatic.

## Tried and ruled out
A cool service baseline is not authority to reuse that temperature in a new guardian. Do not infer that startup caused the higher reading without a controlled measurement, or raise the ceiling merely to complete validation. Matching ordinary Automatic after no mutation is not Auto-restoration proof. No installed Stop success, sustained Stop, sleep/resume or other-model validation follows from this result.

## Watch out
The broker can finish installing the exact payload but report session-agent-relaunch-failed. Inspect installed hashes, SCM state and normal-user processes before assuming rollback. Here the Session Agent was already running and the tray alone was missing; normal tray launch restored the expected process set without reinstalling or killing owners. Preserve the separate earlier directory-lock uncertainty and keep fan-test outcomes explicit.
