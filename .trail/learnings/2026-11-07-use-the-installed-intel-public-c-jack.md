---
title: Use the installed Intel public C API for CPU temperature research
author: Jack
date: 2026-11-07
branch: feat/battery-saver-low-fan-validation
touches:
  - docs/so6120-cpu-temperature-source.md
---

## Decided
The existing Intel IPF public C client can supply changing CPU temperatures without HWiNFO or a new kernel driver. The separately approved RZ09-0581 / Core Ultra 9 386H diagnostic identified enabled TCPU, PCI 8086:B01D, then obtained ten successful readings from 68 to 84 C, each under 17 ms. It disconnected and exited cleanly. This is source and diagnostic evidence, not an integrated or admitted production Stop backend. See docs/so6120-cpu-temperature-source.md for hashes, public sources and limits.

## Tried and ruled out
The older user-mode ipf_lf IOCTL 0x120004 fails with ERROR_INVALID_FUNCTION because the installed UMDF upper filter accepts only the separate event transport. No missing initialization handshake was found; do not bypass the filter or send temperature commands through its event endpoint. The working route uses already installed trusted ipfcore.dll and ipfipc.dll, GetIpfInterface v1 with the required SDK string 1.0.11402, a passive AppInterface v4 participant observer and fixed GET_TEMPERATURE D0/255 requests. Keep the libraries' ordinary participant bookkeeping. Their lifecycle logging-event subscription is not a thermal setting change. HWiNFO offers a separately licensed single-DLL SDK; a normal Pro license does not provide SDK or redistribution rights.

## Watch out
Reject disabled, duplicate, removed or malformed CPU identity and all failed or malformed responses. The successful diagnostic used locked, verified client files, actual loaded-module identity, an isolated worker and independent caller/deadline monitoring. Production integration still needs bounded shared telemetry, explicit unavailable/stale status and power/session/native-lifetime handling so a blocked query cannot prevent Auto restoration. No other model or driver contract inherits this observation. Diagnostic hashes identify reviewed evidence, not a runtime build allowlist. The elevated diagnostic does not establish that production requires elevation. Preserve the shared TZRZ exclusion and immediate missing-CPU Stop exit until a replacement is integrated and validated.
