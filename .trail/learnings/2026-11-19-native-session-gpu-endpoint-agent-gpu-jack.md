---
title: Native Session GPU endpoint (agent-gpu-v1) parity
author: Jack
date: 2026-11-19
branch: feat/native-agent-gpu
---

## Decided
- agent-gpu-v1 implies platform gpu-close; scripted-gpu features never imply it. The build.rs cfg openblade_agent_endpoint covers battery, display-query and gpu.
- Endpoints run under feature_lifetime::run_all(stop, status, Vec<Feature>); run_pair was removed.
- A close that reports, or is stopped after, an unconfirmed submission fences every later close on every connection until a query succeeds.
- Parity oracle in tools/openblade-windows-host/tests/oracles/gpu_apps. responses-verify compares the native frame as parsed JSON, because a managed re-encode of a trimmed non-ASCII list exceeds 64 KiB.
## Tried and ruled out
- Re-encoding native frames with the managed writer for comparison: managed escapes non-ASCII as \uXXXX and overflows the frame.
## Watch out
- Agent-candidate non-test clippy has about 27 findings that predate this work, so CI cannot add -D warnings for that bin. Only the platform gpu-close clippy is gated.
- MSYS sed -i rewrites CRLF C# files as LF. Restore them with perl -pi -e 's/\r?\n/\r\n/'.
- trail check reports keyword false positives on rust.yml lines 12, 15 and 205 and on .editorconfig and .gitattributes, none of them in this diff.
