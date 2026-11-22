---
title: Broker rollback of 4570d54c was the cold-start Setup health probe
author: Jack
date: 2026-11-22
branch: fix/driver-keyboard-deferred-subscription
cost: { minutes: 14, tokens: 163139 }
---

## Decided
The 4570d54c LocalDev broker rollback (request 913543d3, 2026-11-22) was the Setup install health probe, not the change: report serviceHealthy=true, installerExitCode=1, and the diff since the installed ec261c77 touches no Service, Setup or Contracts file. Attempt 1 hit the known first-frame race: Setup took ~2.3 s between connect and header write, and the service's 2 s first-request read timed out (event 4901 FirstRequestReadOutcome=timeout). The reconnect wrote and flushed GetStatus, then read EndOfStream after ~3.1 s: the service closed the status connection without answering. Cold-start GetStatus normally takes 2.0-2.3 s, which is close to that window. A fresh broker request was the chosen recovery.
## Tried and ruled out
The normal token cannot read broker reports or installer-logs, and --deployment-status only shows the in-flight request. One attended elevated read-only copy (Start-Process -Verb RunAs, copy reports/journal/installer-logs, grant the user read) exported them in seconds. Windows events alone showed only the service start and stop.
## Watch out
installer-logs/<id>/index.json maps log-NN.log to Burn, Setup and the rollback package logs. Setup's "service health timing" trace lines carry QPC values you can join to the service's 4901 event. The server swallows IOException and TimeoutException from the first connection, including from handling, so a handler timeout during cold start looks to the client like a silent close. That is still an open hypothesis for the EOF.
