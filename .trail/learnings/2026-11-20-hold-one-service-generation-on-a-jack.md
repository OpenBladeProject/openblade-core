---
title: Hold one Service generation on a separate transport thread
author: Jack
date: 2026-11-20
branch: feat/native-agent-application-reporter
touches:
  - tools/openblade-windows-host/src/session_native/service_client.rs
  - tools/openblade-windows-host/src/session_native/application.rs
  - tools/openblade-windows-host/src/session_native/restart.rs
---

## Decided

The report transport runs on its own thread and runtime, and holds one verified Service generation across reports. Acquiring a generation re-reads and hashes the installed package and both images, 32 to 119 ms, so doing it for every five-second renewal on the shared Agent runtime stalled display, GPU and battery. The generation trait objects are not Send, so isolating the whole transport thread was the fix, not spawn_blocking. A report verifies the held generation and acquires another only after it is retired or a verification fails; an I/O failure keeps it. The owner thread is woken by the watcher's notice hook, by completions and by profile changes, with a 500 ms housekeeping tick, because polling at 100 ms plus a 25 ms hook pump cost about 50 wakeups a second. The installed entry registers Windows Application Restart as managed does, and the monitor unregisters it on a deliberate stop.

## Tried and ruled out

Do not put a completion in the same one-entry channel as wakes: a wake can fill it and the completion is dropped, which ended the role. Completions have their own slot. Do not share the generation cache with request_on or watch_status: the installed Agent has no request_on caller and the subscription already holds its generation for the stream. Do not bound the reply by the whole exchange or leave it unbounded: managed bounds only the connect, and an unbounded reply would hold the single in-flight report and every renewal.

## Watch out

An error return from the Agent is an ordinary exit, which Windows Application Restart does not restart, so a role failure still leaves the Agent down until the next sign-in. Managed reaches a restart because its supervisor failure is an unhandled exception. Whether the native Agent should crash on a role failure is undecided.
