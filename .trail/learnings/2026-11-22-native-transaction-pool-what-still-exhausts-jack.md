---
title: "Native transaction pool: what still exhausts it, and fan pin timing"
author: Jack
date: 2026-11-22
branch: fix/native-transaction-pool-reclaim
---

## Decided
- The native transaction pool is reclaimed, so every learning that counts a fixed process budget is out of date. That covers "each query costs one pair of the 127-pair process transaction budget" (2026-11-22 native read-only keyboard Driver mode), the 15m45s, 10m30s and 7.9 min exhaustion times (2026-11-22 Driver-mode read round 2), "the queries alone exhaust the 127-pair transaction budget after about 31 minutes" (2026-11-22 feature-gated read-only keyboard Driver mode), "the finite transaction budget ... remain separate work" (2026-11-13 thermal retirement settlement) and "preserve ... the finite transaction pool across workers" (2026-11-12 installed mutation ambiguity). The pool is still one process-wide pool across workers, but answered and unsent IDs now return to it. Those notes are left unedited.
- What can still exhaust the pool, measured on the in-memory port: about 12 manual fan set-point changes (20 to 24 pinned IDs each), 14 manual/Automatic alternations, 30 temporary-charge enable and cancel cycles (8 pinned cancel IDs each), or about 253 reads whose exchange keeps failing (one quarantined ID each: 8.5 min at the 2 s subscription poll, 21 min at 5 s). Answered reads never exhaust it. The follow-up is to release a record's pins when its owner settles, in fan_mutation.rs and battery_mutation.rs.
- A fan record pins its IDs only once a setter may reach the device: `retain()` runs after the pace check before the first setter, not after `prepare_fan`. A pace-refused manual request used to pin 24 IDs with nothing sent, so 10 such requests exhausted the pool for fans.
- The process pool (`Transactions::randomized`) starts its round at a random ID, drawn once from `RandomState`. Every process used to issue ID 1 first, the same ID a short-lived previous process left in the mailbox. Tests use `Transactions::new()` (start at 1) or `seeded(seed)`.

## Tried and ruled out
- Relying on the old setter-free tests. The mailbox model panicked on `SendMutation`, so nothing pinned `sending()` for setters or its order before `submit`; it now echoes 0D/02. The terminal-reply test flipped class and command bytes without recomputing the checksum, so only the checksum was under test. A test that checks the command but not the class, or the reverse, must assert that the wrong answer frees nothing (`an_answer_to_another_class_or_command_frees_nothing`), not an exact free count.

## Watch out
- `Transactions::poison` (test-only) poisons through `catch_unwind`, so its panic message shows in test output on purpose.
- A test that checks an ID is "never issued again" must look only at sends after the point in question. A wrapped pool reissues the same ID to earlier requests.
- The reviewer's mutants P1, P2, TC1, TC2, T4, T15, T20b and T20c survived every lane before these tests. Rerun them after changes to `Attempt::transfer`, `temporary_charge.rs` or `transactions.rs`.
