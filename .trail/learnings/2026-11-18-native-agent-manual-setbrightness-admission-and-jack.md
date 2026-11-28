---
title: Native Agent manual setBrightness admission and recovery
author: Jack
date: 2026-11-18
branch: feat/native-agent-manual-brightness
cost: { minutes: 29, tokens: 399429 }
touches:
  - captures/
  - tools/openblade-windows-host/tests/fixtures/display-automation/managed-checkpoints.jsonl
  - tools/openblade-windows-host/tests/oracles/display_query/Program.cs
  - tools/openblade-windows-host/tests/oracles/display_query/README.md
  - tools/openblade-windows-host/src/service_status.rs
  - tools/openblade-windows-host/src/session_native.rs
  - tools/openblade-windows-host/src/session_native/display_query.rs
  - tools/openblade-windows-host/src/session_native/display_query/brightness_lifetime_tests.rs
  - tools/openblade-windows-host/src/session_native/display_query/brightness_write.rs
  - tools/openblade-windows-host/src/session_native/display_query/brightness_write_tests.rs
---

## Decided
- Native Agent manual setBrightness is a separate feature, agent-display-brightness-write-v1 (implies the query feature), with its own package marker session-display-brightness-write-partial-v1. Query-only builds and markers are unchanged.
- The submission fence lives in the shared crate (SubmissionFence on CallBudget). Submission::begin re-reads CCD topology and asks the host to admit after preparation and before the request gate. The host fence checks Origin.verify, the active console session (WTS) and the power generation.
- Power admission comes only from fresh, authenticated Service status. Every newly opened subscription is a new generation, and so is a changed or unknown source. Stop, Origin loss or a watcher exit retires it permanently, before the feature drains.
- Recovery writes the managed display-automation.json schema 4. A native-only brightness gate stays set (including across restart) until an explicit read observes brightness. The managed resume needs every uncertain field confirmed, and that can never happen when HDR is unsupported.

## Tried and ruled out
- Treating a worker start failure as WorkerDisconnected. Only a lost reply from an existing worker is fatal. A start failure maps to ControllerUnavailable, which is non-fatal.

## Watch out
- session_native tests are compiled only into the agent bin target. Run them with --bin openblade-windows-agent-candidate; --lib matches 0 tests.
- Frame-corpus capture helpers must share one lock. With two statics, parallel tests interleave JSONL rows and the managed oracle rejects the corpus.
- The native checkpoint writer emits keys in alphabetical order (serde_json Map), not the managed declaration order. The managed reader accepts this, as the oracle proves, but the bytes are not byte-identical.
- A bash heredoc containing a Rust lifetime ('de) aborts. Write the file with the Write tool instead.
- trail check on this branch flags .gitattributes and .editorconfig lines inherited from earlier migration stages. These are lexical hits, not conflicts.
