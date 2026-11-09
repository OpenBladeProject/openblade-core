---
title: Verify installed rolling-policy cleanup without claiming Stop
author: Jack
date: 2026-11-08
branch: feat/battery-saver-low-fan-validation
touches:
  - docs/so6120-stop-auto-guardian.md
---

## Decided
The exact e820aed2 rolling-average build was installed through the enrolled broker. All 372 installed payload files and the Service, Session Agent and Tray generation matched; typed IPC77 readback preserved the original Custom55/55, five-second window and disabled automatic entry, adding migrated 65/65 immediate cutoffs.

The authorized Custom65 repeat changed only the CPU average limit. It issued one Stop, observed Starting followed by ReturningToAutomatic without Stopped, and issued one Auto. The Applied Auto receipt and independent public Automatic readback verified same-operation cleanup/release. Every original setting was restored and independently matched; final readings showed spinning fans.

## Tried and ruled out
Neither an Applied Stop receipt nor successful Auto cleanup establishes that Stop was reached. Do not turn a sequence that lacks Stopped confirmation into a successful fan-stop or five-second-dwell result. The generic completion message does not identify which condition ended entry, and later CPU heat does not prove the cause.

## Watch out
The trial preserves both immediate cutoffs. With the requested CPU average equal to its existing 65 C immediate cutoff, the raw cutoff remains authoritative. Before live execution, rebuild test-client dependencies at the committed source when their metadata still reports the pre-commit revision; an actual IPC77 constant alone does not establish exact source provenance. No repeated Stop, settings widening or further hardware write follows this result automatically.
