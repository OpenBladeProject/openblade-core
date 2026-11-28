---
title: Make the Agent restart admissible and fail fast on a fault
author: Jack
date: 2026-11-21
branch: feat/native-agent-application-reporter
touches:
  - tools/openblade-windows-host/src/session_native/restart.rs
  - tools/openblade-windows-host/src/session_native/exit.rs
  - tools/openblade-windows-host/src/session_native.rs
  - tools/openblade-windows-host/src/loaded_origin.rs
---

This supersedes the Watch out in 2026-11-20-hold-one-service-generation-on-a-jack.md: the restart registration there was inert.

## Decided

The Application Restart registration now works the way managed does. It registers a NULL command line, because Origin admission refuses any argument and a restart that carried one exited at once. The working directory is not an admission rule: the sign-in action sets it, a restart cannot, and Origin has already proved the image, package, process and session. The Agent now sets its working directory to the verified install directory instead of refusing, which is safe because nothing before that point resolves a relative path and SetDefaultDllDirectories already removed the working directory from DLL search. A role fault or a panic finishes draining, drops Origin and custody, reports its diagnostic and then fails fast, so Windows Error Reporting sees a crash and restarts an Agent that ran at least 60 seconds. Admission denials, a lost singleton, an origin retired under the Agent and a deliberate stop stay ordinary exits.

## Tried and ruled out

Do not register managed's --application-restart: native admission denies it. Do not keep the working-directory equality as a gate: Windows cannot satisfy it for a restart, and dropping it without binding the directory would leave a user-writable current directory. Do not treat an error return as a restart: it is an ordinary exit.

## Watch out

Whether a restart really happens, and passes admission from a different working directory, has not been tried. It needs an attended crash after 60 seconds, a crash before 60 seconds and a deliberate stop. A status or authority failure after admission is a fault, so it fails fast.
