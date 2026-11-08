---
title: Audit safe span wrappers before crossing the native ABI
author: Jack
date: 2026-11-08
branch: feat/rust-production-activation
touches:
  - src/OpenBlade.Core.Native/NativeReportCodec.cs
  - tests/OpenBlade.Core.Native.Tests/NativeEncodeIntoTests.cs
---

## Decided
Audit all safe span-based Core.Native entry points independently of RazerReport's legacy projection. NativeReportCodec.EncodeInto is the only public caller-supplied writable-span route. It always passes a separate90byte stack output to RawEncode, then copies to the caller destination after all FFI input reads finish. Its explicit contract permits overlapping managed payload/destination and encodes the original payload snapshot.

Encode and ValidateResponse allocate fresh native output arrays. ValidateResponse's two immutable input spans may alias one another: the actual v1 C ABI explicitly permits immutable-input aliasing while requiring every writable range, including output/metadata, to be disjoint. No ValidateInto exists. NativeProfilePolicy public APIs take host records; they construct separate input/output arrays and metadata internally. Private span readers do not cross FFI. Raw pointer helpers remain unsafe caller-contract APIs.

## Tried and ruled out
Do not claim a safe wrapper enforces ABI disjointness merely because its production caller does. Do not pass the exposed destination span directly to FFI or reject immutable input aliasing that ABI v1 permits. No ABI change or additional Claude run is needed: the existing separate output storage already provides the required projection.

## Watch out
Seven focused span tests pass: existing allocation/failure behavior, four direct overlapping EncodeInto geometries with untouched outside bytes and original input expectations, and aliased immutable validation inputs with independently owned returned payload. Source review covers the disjoint stack/array metadata lifetimes; tests alone do not prove pointer provenance or legalize aliased unsafe raw calls.
