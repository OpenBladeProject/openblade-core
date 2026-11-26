---
title: Native keyboard lighting L1 gates and status projection
author: Jack
date: 2026-11-26
branch: feat/native-lighting
---

## Decided
- Native lighting L1 (`service-lighting-v1`) admits only Off plus logo modes; lit effects are stored then refused with managed's capability text until L2.
- Lighting reads no adapter, so on AC it requires managed's unclassified admission (fresh Windows AC agreeing with status, HyperBoost not engaged) via `status_query_power`. Status power 3 is coarse external, not "unclassified": gating on `== 3` blocks all AC.
- A lighting reading joins only an existing confirmed state, and is also written into the reader's retained thermal read (`ReadSource::record_lighting`), because `decorate` re-projects status from that read on every get.

## Tried and ruled out
- Sending lighting requests through the rig's `serde_json::Value` path: it sorts `kind` out of first place in `parameters` and the bind refuses it. Use `Rig::request_payload` with ordered bytes.

## Watch out
- Saved policy (including lighting) shows in status only after a policy-retaining request in this process; a restart test must check the store, not getStatus.
- The next thermal observation publishes lighting members as null (L2 adds the periodic read).
