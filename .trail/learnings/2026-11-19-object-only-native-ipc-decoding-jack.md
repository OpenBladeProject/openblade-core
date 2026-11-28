---
title: Object-only native IPC decoding
author: Jack
date: 2026-11-19
branch: fix/native-service-request-object-only
touches:
  - tools/openblade-windows-host/src/json_object.rs
  - tools/openblade-windows-host/src/service_status.rs
  - tools/openblade-windows-host/src/reporter_pipe.rs
  - tools/openblade-windows-host/src/session_native/display_query.rs
  - tools/openblade-windows-host/tests/fixtures/service-object-frames.json
  - tests/OpenBlade.Contracts.Tests/IpcMessageCodecTests.ObjectFrames.cs
---

serde's derived structs, and internally tagged enums such as StatusRequest (tag = "operation"), also accept a JSON array and bind it by position. deny_unknown_fields does not change that. Before this fix the native Service accepted ["getStatus",80,"<id>"] and a setBatteryPolicy value of [80]; the managed IpcMessageCodec rejects both (now pinned by IpcMessageCodecTests.ObjectFrames.cs over tests/fixtures/service-object-frames.json).

Rule: decode every IPC payload with openblade_windows_host::json_object::from_slice, and give every nested member whose type is a derived struct `deserialize_with = "object"` or `"nullable_object"` (or the Object<T> type). Scalars, strings, Map and Value members need nothing. A deserialize_with member is required: add `default` to keep an Option member optional. Flatten structs (Snapshot, Profiles, PowerProfile, DesiredState) are already map-only, but their non-flatten nested structs are not.

Managed request bytes put the operation first and version/requestId last (base record members). Native serialization puts version/requestId first, so do not compare a native re-encode with managed bytes; compare the object-only parse with plain serde instead.

Not changed: Display (operation_first already requires `{`; nested settings use Object) and Battery.v1 (hand-written map visitor) were already object-only. Development-only replay decoders (wire.rs, session_gpu/audio/display, application_runtime) still use bare derives. A unit enum like the response `frame: Frame` still accepts serde's externally tagged map form, which is a different gap.

At base 265594e5 the Service candidate suites already fail 21/4/4/14 tests (temporary-charge/metadata/hardware/thermal) and Contracts.Tests fails 26, mostly v79 WindowsReplay fixtures; compare failure lists, not totals.
