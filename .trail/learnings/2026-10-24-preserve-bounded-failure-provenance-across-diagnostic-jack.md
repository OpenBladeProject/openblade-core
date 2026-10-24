---
title: Preserve bounded failure provenance across diagnostic boundaries
author: Jack
date: 2026-10-24
branch: fix/diagnostic-failure-provenance
touches:
  - Directory.Build.props
  - docs/developer-guide.md
  - docs/snap-tap.md
  - src/OpenBlade.SessionAgent/Program.cs
  - src/OpenBlade.SessionAgent/RazerSnapTapDriverAdmission.cs
  - src/OpenBlade.SessionAgent/RazerSnapTapHookSession.cs
  - src/OpenBlade.SessionAgent/SessionAgentPipeServer.cs
  - src/OpenBlade.SessionAgent/SessionBatteryRequestDispatcher.cs
  - src/OpenBlade.SessionAgent/SnapTapDriverInputProcessor.cs
  - src/OpenBlade.SessionAgent/SnapTapDriverLoop.cs
---

## Decided
Preserve failure provenance at the boundary where it would otherwise become an enum, null or exit code. The shared DiagnosticFailure record contains bounded component/operation/stage/reason tokens, exception type/HResult/Win32/process exit and up to four causes, plus possible submission; it excludes messages, paths, command output and input data. SnapTap carries terminal detail worker-to-supervisor-to-parent only for the matching process and fresh attempt, preserving the original cause through cleanup. Battery report and unexpected Session Agent dispatch failures use the same safe metadata. Diagnostic metadata has no hardware or retry authority.

## Tried and ruled out
Adding only more admission logging cannot diagnose post-admission failure. Generic UI text need not expose technical internals, but it must not be the only durable evidence. Do not add per-key disk logging, replace the existing status channel, or redesign already informative service IPC. Do not use full Exception.ToString or raw powercfg output in exported records.

## Watch out
Status schema v1 tolerates the optional failure property; test legacy payloads. A clean helper stop must not erase an earlier supervision failure. Failure causes and possible-submission state survive reconciliation. Standard exports include snap-tap.json and diagnostic-export.json; log files are read separately with file/count bounds so one unreadable log cannot silently discard the rest. SaveExact identity exports remain byte-for-byte unchanged. This fixes observability; it does not establish the root cause or physical recovery of an owner-reported SnapTap failure.
