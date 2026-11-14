---
title: Keep peer refusal local and native Agent diagnostics retained
author: Jack
date: 2026-11-14
branch: feat/native-agent-battery-report
touches:
  - rust/crates/openblade-platform-windows/src/battery_report.rs
  - rust/crates/openblade-platform-windows/src/battery_report/custody.rs
  - rust/crates/openblade-platform-windows/src/battery_report/tests.rs
  - rust/crates/openblade-platform-windows/src/lib.rs
  - tools/openblade-windows-host/src/installed_agent_main.rs
  - tools/openblade-windows-host/src/session_native/battery.rs
  - tools/openblade-windows-host/src/session_native/battery/tests.rs
  - rust/crates/openblade-platform-windows/src/agent_diagnostics.rs
---

## Decided
The native Agent Battery.v1 correction separates accepted-connection errors from fatal authority/custody errors by source, not by io::ErrorKind. Refused peer or bad-frame I/O drops only that connection; installed-origin, listener and unconfirmed-retirement failures remain fatal. Test the actual serial loop with a rejected peer followed by a successful legitimate peer, and an origin failure after request read that prevents a second acceptance. Keep fresh SID/session/LUID checks and the RevertToSelf abort fence.

Shared ancestor data-read pins permit SHARE_WRITE while withholding SHARE_DELETE. This applies to report ancestors and the launch-local System32 ancestry helper. Private attempt directory, report file and exact executable pins retain their distinct contracts. A metadata-only pin is not an equivalent no-delete fence: Root's owned Win32 experiment admitted DELETE. Test explicit directory ADD_FILE access in both open orders, ordinary child write, and actual rename/delete refusal separately.

The selected native Agent registers a lifetime-owned typed persistent sink in the existing current-user OpenBlade/Logs export path. Only finite cleanup, retirement, origin and endpoint events are recorded, with error kind/optional OS code where applicable; never report XML, identities, paths or raw exception strings. Current and previous log segments are each bounded to 1 MiB. Before explicit registration, record is inert. Cleanup logging remains independent of the valid report or original launch error.

## Tried and ruled out
Do not blanket-ignore listener errors or change locked mio ERROR_NO_DATA handling; Root refuted that allegation. Do not drop the retained Child+Attempt guardian just because kill-on-close exists: termination request is not confirmed retirement. Do not infer all child creation was blocked by shared ancestor pins, or extend launch-local handles to the full report lifetime.

## Watch out
Actual powercfg output-sharing compatibility and installed peer/origin/lifecycle behavior remain untested. Root's legacy cleanup-residue observation matches managed best-effort deletion; no sweeper, cleanup journal or reader-handle bypass is added. The prior seals remain immutable. Fresh native command output and dispatcher corpus/managed decoding belong to the correction; earlier 27e test counts remain separately qualified historical evidence.
