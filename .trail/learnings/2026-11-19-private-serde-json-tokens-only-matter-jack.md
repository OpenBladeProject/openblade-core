---
title: Private serde_json tokens only matter as member names
author: Jack
date: 2026-11-19
branch: fix/native-service-request-object-only
touches:
  - ".github/workflows/rust.yml"
  - tests/OpenBlade.Contracts.Tests/IpcMessageCodecTests.ObjectFrames.cs
  - tools/openblade-windows-host/src/json_object.rs
  - tools/openblade-windows-host/src/service_status.rs
  - tools/openblade-windows-host/src/service_status/object_tests.rs
  - tools/openblade-windows-host/src/session_native/display_query/recovery_tests.rs
  - tools/openblade-windows-host/src/session_native/display_query/request_parity_tests.rs
  - tools/openblade-windows-host/tests/fixtures/display-automation/managed-checkpoints.jsonl
  - tools/openblade-windows-host/tests/fixtures/display-requests/managed-requests.jsonl
  - tools/openblade-windows-host/tests/fixtures/service-object-frames.json
---

Corrects the private-token learning from earlier today: refusing every string that starts with `$serde_json::private::` was a regression. serde_json reinterprets that prefix only as a member name.

## Decided
- `json_object::scan` refuses a decoded string with the token prefix only when the next non-whitespace byte is `:` (a member name). Values with the prefix are ordinary text: a user-chosen application profile name, a Display refresh or color key, a status message. Managed writes and accepts all of these; the earlier scan made the native Agent refuse a response its own Service wrote.
- Display recovery keeps the shared scan. Its saved Baseline keys are user-chosen Display keys, so it had the same bug; a managed checkpoint row with prefix keys now pins it.
- Fixture evidence: Service rows with prefix values (profile name in get-status and the event, message, potentialControllers, controllerDetectionWarning) are valid on both sides, and the managed test re-encodes every valid server row to the same JSON. Display oracle rows for setRefreshRate, setColorProfile and applyProfile keys are canonical managed requests.
- Integer `-0` in typed status members stays refused natively (serde_json reads it as a float); managed reads it as 0 but never writes it. The event `version` reads through `serde_json::Number`, so `-0` reports the version mismatch, as managed does with its version-mismatch exception.
- The CI step captures the cargo exit code with `$PSNativeCommandUseErrorActionPreference = $false`, prints the output, then throws, and runs an Agent build with Display writes so the recovery and Display parity tests run too.

## Tried and ruled out
- Refusing token text in values "for safety": serde_json never reinterprets a value, so it only broke valid frames.
- Making the Display root use `from_slice` observable: every Display envelope member is typed and `deny_unknown_fields`, so a plain `serde_json::from_slice` refuses the same frames; that mutant is equivalent.

## Watch out
- A scan change must be tested both ways: names refused (including whitespace before `:` and escapes) and the same spellings accepted as values. `private_token_values_decode_and_names_are_refused` does that.
- After a mutation run, touch the restored file; cargo otherwise keeps the mutated build because the restored copy has an older mtime.
