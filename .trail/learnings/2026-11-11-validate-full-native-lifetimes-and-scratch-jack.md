---
title: Validate full native lifetimes and scratch identity consumers
author: Jack
date: 2026-11-11
branch: main
touches:
  - debug.log
---

## Decided
Validate Claude findings against the complete exact commit, including the concrete types and enclosing async scopes missing from a patch. The Phase 10i shutdown-deadlock claim is false at befbcdfde21876dbceb48eb42a2084398b0655de and 417de01723af139c33e8bfc4c6d32db1358eb845: serve_connections owns the pinned refresh future and returns before drive calls retire, so its mutex waiter/guard is dropped first. The Phase 10n retry-release claim is also unreachable with the concrete Worker: stop sets stopped and retains shared unconfirmed failure across every clone; no later stop can succeed and reach owner release.

The reviewed Rust SET_FEATURE buffer placement matches WindowsHidNative.Submit: report in the input buffer, null output. The local Microsoft SDK defines SET_FEATURE as HID_IN_CTL_CODE and GET_FEATURE as HID_OUT_CTL_CODE. The contrary Claude claim depended on unverified recollection. Preserve established managed semantics unless stronger primary evidence or an authorized exact-device test contradicts them; this review did not physically run the Rust adapter.

A framework-dependent scratch receiver is more than its apphost. Retain and bind its complete app-local closure and loader configuration, seal its namespace for that lifetime, and verify the same semantics in every consumer. The C# closure fix at 68d6f696 changes launch-evidence bytes 56..88 from apphost hash to canonical closure hash. The older Rust reader still hashes the apphost alone, so cross-language closure verification must be updated before claiming a working handshake. Keep these scratch-only ACL and identity rules out of installed admission.

## Tried and ruled out
Do not repair the assumed mutex deadlock by changing an already correctly scoped drain, remove native lifetime fences, reverse IOCTL buffers from a generic method-bit assumption, or claim C# fixture success proves Rust interoperability. Do not treat an invalid-length legacy provisional request as coverage of authenticated issuer rejection paths.

## Watch out
Use independent C#-Rust known-answer fixtures and named offline failure tests. Distinguish native process/pipe validation still deferred from in-memory or filesystem evidence. The current native candidate remains closed pending real package commitment, loaded generation, sole ownership and live safety evidence. Check actual Cargo target settings and compiler output; the scratch fixture has test=false, while a separately observed scratch-only build fails because its root omits pipe_server_identity.
