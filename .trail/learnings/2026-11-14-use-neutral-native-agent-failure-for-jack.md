---
title: Use neutral native Agent failure for unclassified terminal errors
author: Jack
date: 2026-11-14
branch: feat/native-agent-battery-report
touches:
  - rust/crates/openblade-platform-windows/src/agent_diagnostics.rs
  - tools/openblade-windows-host/src/session_native/diagnostics.rs
---

## Decided
Use the neutral fixed AgentFailure event for an unclassified native Agent terminal error. A working-directory or token admission failure can occur before any battery listener is attempted, so generic Endpoint wording invents a cause. Same I/O kinds and words in an error message do not establish Origin or Listener provenance; keep those explicit tags, BatteryRetirement tags and attempted-delivery handling unchanged.

## Tried and ruled out
Do not expand the phase taxonomy or add origin-false/mismatch guards to address this naming defect. Root calibrated the optional post-reconciliation terminal summary as an observability enhancement, not a required fix. The selected error wrapper's changed outer raw/message surface has no demonstrated consumer regression in the examined closure; do not redesign it on speculation.

## Watch out
The default-label regression should exercise actual terminal routing with unclassified same-kind entry errors, then compare explicitly tagged same-kind causes. Reuse unchanged wire/parser/platform-store evidence. Diagnostic history is not current lifecycle or retirement proof, and attempted delivery is not proof either destination retained bytes. No installed startup, production log registration, supplier or hardware action is needed for this correction. Preserve earlier source/evidence seals.
