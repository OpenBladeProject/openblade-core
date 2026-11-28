---
title: Same-session lock and desktop changes keep retained application processes
author: Jack
date: 2026-11-20
branch: feat/native-agent-application-reporter
touches:
  - rust/crates/openblade-platform-windows/src/application/watcher.rs
  - rust/crates/openblade-application-reporter/src/lib.rs
---

This supersedes the session-loss rule in 2026-11-10-fence-private-application-reports-with-foreground-jack.md, which said to release local evidence on session loss and not renew the lease.

## Decided

A lock, a secure-desktop switch or another user on the console does not end the signed-in user's processes, so the foreground watcher keeps its retained process handles and keeps renewing through them. Only the foreground becomes unknown. This is what managed does: an unknown foreground leaves the tracked processes alone, only a process exit removes one, the five-second tick renews whatever state the session is in, and the Service accepts reports from the locked console session. A Foreground profile ends with its window, so its context is cleared while locked and the window is sampled again on unlock. A desktop switch forgets only the foreground and forces a fresh sample. A different active session id is a different user: every retained process is forgotten and the old context is cleared. The leaf has two calls for this, forget_foreground and forget_process_context.

## Tried and ruled out

Do not read the session as lost whenever active_local_session is empty. The active session is always the Agent own session, so an empty answer is a lock, a secure desktop, a disconnected or switched-away console, never a session change of this process.

## Watch out

The Service session rule for reports came from the managed InteractiveSessionAuthority, not from the native Service fixture, whose private v85 replay still refuses reports while the session is inactive. Keep that difference in mind when comparing the two.
