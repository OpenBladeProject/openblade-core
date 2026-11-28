---
title: Private serde_json tokens and buffered numbers in native IPC decoding
author: Jack
date: 2026-11-19
branch: fix/native-service-request-object-only
touches:
  - ".github/workflows/rust.yml"
  - tests/OpenBlade.Contracts.Tests/IpcMessageCodecTests.ObjectFrames.cs
  - tools/openblade-windows-host/src/json_object.rs
  - tools/openblade-windows-host/src/service_status.rs
  - tools/openblade-windows-host/src/service_status/object_tests.rs
  - tools/openblade-windows-host/src/session_battery.rs
  - tools/openblade-windows-host/src/session_native/battery.rs
  - tools/openblade-windows-host/src/session_native/display_query.rs
  - tools/openblade-windows-host/src/session_native/display_query/recovery.rs
  - tools/openblade-windows-host/tests/fixtures/service-object-frames.json
---

Corrects the earlier object-only learning: "Value members need nothing" was wrong for this crate, because serde_json is built with `arbitrary_precision` and `raw_value`.

## Decided
- `json_object::from_slice` now scans the raw bytes first and refuses any string, member name or value, that decodes (escapes included) to a `$serde_json::private::` prefix. Managed reads such a member as an ordinary one; serde_json reads `{"$serde_json::private::Number":"1"}` as the number 1 and `{"$serde_json::private::RawValue":"..."}` as re-parsed JSON, which also hides duplicate members from `validate_json_members`.
- The scan is shared: `json_object::scan` returns depth and an integer `-0` flag. Display recovery keeps its stricter use (depth limit, refuse `-0`). Service, reporter and Display decoders use `from_slice`; the Battery decoders call `json_object::public_tokens` before their own map-only visitor so array error text stays unchanged.
- `Object<T>` refuses private member names too. serde buffers a non-integer or out-of-range number (1.5, 1e2, -0.0, 30 digits) inside tagged or flattened content as that private one-entry map, and an all-optional struct or a Map took it as an object.
- The Agent status event is a plain struct with a `frame` string, not a tagged enum, so its snapshot is never buffered and Map members refuse numbers directly.
- Absent `liveReadings` still decodes to none natively (managed requires it); pinned by a test, not changed.
- CI runs the candidate `service_status::object_tests` in its own Windows step, since the default test run builds neither candidate.

## Tried and ruled out
- Wrapping every Map member: unnecessary once the event is unbuffered; flatten structs read known members directly from the parser.
- Parsing fixture rows into `Value` to splice tokens: the parse itself converts the token. Both test sides splice the row text instead.

## Watch out
- A `Value` member still takes a buffered 1.5 as the number 1.5; that is correct, not a token leak.
- Tool inputs here decoded backslash-u escapes in test strings; check the file bytes after writing an escape row.
