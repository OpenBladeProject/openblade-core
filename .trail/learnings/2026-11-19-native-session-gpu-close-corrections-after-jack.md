---
title: Native Session GPU close corrections after review
author: Jack
date: 2026-11-19
branch: feat/native-agent-gpu
---

## Decided
- There is no endpoint-wide fence after an unconfirmed GPU close. The Tray (GpuAppCleanupCoordinator) force-closes one app per request with no query in between. Every close does its own fresh scan and re-reads identity on the held handle, so the uncertainty stays in that request's results. This replaces the fence in the 2026-11-19 native Session GPU endpoint learning.
- In a force-close tree walk, each descendant handle stays held until its children are enumerated (as managed KillTree does), so a terminated PID cannot be recycled mid-walk.
- Force close reads the token's mandatory integrity RID on the held handle and acts only at or below the Agent's own level. Managed cannot open higher-integrity processes with PROCESS_ALL_ACCESS, and an unreadable level counts as a refusal. Graceful close does not check integrity.
- The action names the held image with sampling::executable_stem, the scan's own projection.
## Tried and ruled out
- Building the native Report from the fixture's expected results: that test is circular. The platform test managed_dispatch_close_rows_are_derived_by_the_decision replays the fixture rows through decide instead.
- Adding serde_json as a platform dev-dependency: Check-Workspace.ps1 admits fixture parsers only in openblade-protocol and openblade-domain. The platform test uses a small JSON reader in native/tests/json.rs instead.
## Watch out
- installed_monitor wiring compiles only in non-test builds. CI runs cargo check on the agent bin with agent-gpu-v1 to cover it.
