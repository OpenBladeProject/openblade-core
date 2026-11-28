---
title: Run the application reporter in the installed native Agent
author: Jack
date: 2026-11-20
branch: feat/native-agent-application-reporter
touches:
  - tools/openblade-windows-host/src/session_native/application.rs
  - tools/openblade-windows-host/src/session_native/application/owner.rs
  - tools/openblade-windows-host/src/session_native/service_client.rs
  - tools/openblade-windows-host/src/native_package.rs
---

## Decided

Run the application reporter in the installed native Agent as `agent-application-reporter-v1`. The watcher runs on its own owner thread, so its blocking Win32 calls never touch the Agent runtime. That thread applies the latest verified profile snapshot, polls the platform watcher every 100 ms and hands at most one request at a time to an async transport. The transport sends one short connection per report. It proves the installed Service generation and pipe peer before sending and again before trusting the reply, and returns only Stored and Applied as acknowledgements. Failure, refusal and a stopped Service are repeated by the policy's five-second renewal with a fresh sequence and request id, as managed does. The Agent stop signal drops a pending report, retires the watcher and joins the owner thread before status and Origin custody are released. A stop that arrives during startup waits at most the startup budget, because the platform never joins a registration thread that is still inside a native call.

## Tried and ruled out

Do not call the policy's disconnected and reconnected hooks from the status subscription. Managed has no connection state: its reports are independent connections, so pausing them while the subscription reconnects would let the Service lease lapse that managed keeps alive. A Service restart is learned at the next renewal. Do not run the scripted platform watcher across threads in tests: its script is thread-confined, so the composition tests build it inside the owner thread and steer it with commands applied before each poll. Dropping a pinned future through `tokio::pin!` does not drop the future, so use `Box::pin` when a test must cancel by drop.

## Watch out

The feature is refused by name, feature and marker, in both package readers until a packaging stage admits it. A new admitted marker must keep the fixed order in build.rs. The installed monitor is not compiled by tests; only the CI check lane proves its wiring, so a lane for any new feature combination must be added there. A watcher that cannot start or retires itself ends the Agent, as every other selected role does. A status whose application profile list cannot be read keeps the earlier list. Live foreground hooks, a real Service pipe, a Service restart and the poll cost are still unvalidated.
