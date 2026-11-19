---
title: native build identity bound and the package record chain
author: Jack
date: 2026-11-19
branch: fix/native-identity-record-headroom
touches:
  - tools/openblade-windows-host/build.rs
  - tools/openblade-windows-host/src/native_package.rs
  - tools/openblade-windows-host/src/native_package/identity_bound.rs
  - src/OpenBlade.Deployment/WindowsNativePackage.cs
  - tools/openblade-windows-host/tests/data/native-identity-bound-v1.json
---

The `.obid` build identity was bounded at 1024 bytes in four places (build.rs assert, `compiled_record`, `inspect_artifact`, managed `NativeArtifact.Inspect`), and the worst shippable composition (service-temporary-charge-v1 plus all six Agent features, release) reached 977. It is now 4096, stated once per language: `MAX_IDENTITY_BYTES` in `tools/openblade-windows-host/src/native_package/identity_bound.rs`, which build.rs and native_package.rs both `include!`, and `NativeArtifact.MaximumIdentityBytes` in C#. `the_identity_bounds_match_the_managed_and_build_script_bounds` pins the C# value and the build.rs include.

The `.obid` cap was not the only bound. The package record carries each artifact record (identity + size + digest + imports) as base64, and that field was capped at 2048 characters by the general `NativeCanonical.Lines` field bound in C# and at 4096 by `decode64` in Rust. With the measured imports, that capped the Agent identity near 1259 bytes, so raising only the `.obid` cap would have moved the failure into `WindowsNativePackage`'s constructor. Both languages now share `MaximumRecordBase64` / `MAX_ARTIFACT_RECORD_BASE64` = 10924 (base64 of up to 8193 bytes), and NativeArtifactRecord fields may fill an identity (the feature line alone can pass 2048). Any future identity growth has to check this whole chain: identity, artifact record field bound, package base64 field bound, and the 16384-byte package total.

Measured from bound release images built from this change (parsed, never run): Agent identity 977 bytes, record 1254, base64 1672, imports 198 characters; Service identity 927, record 1188, base64 1584. With both identities at 4096 the package is about 12159 of 16384 bytes, so the package total is not the binding limit; the identity has 3119 bytes of headroom, roughly 44 more Agent capabilities at about 70 bytes each.

`native-identity-bound-v1.json` holds inert PE headers with identities at 1025, 4096 (Agent and Service) and 4097 bytes; both languages check the same bytes. Its generator was a scratch perl script, not checked in: the identity is the worst composition padded with `agent-reserved-capability-NNN-v1` names and one `agent-reserved-fill-x...` name. Regenerate it if the bound changes. The test helper `bytes()` decodes through `decode64`'s 4096-character bound, so larger KAT values need `decode64_bounded`.

Compatibility: bounds only widened, so every record an old reader wrote still parses. A record or image written under the new bounds and larger than the old ones is refused, fail closed, by an older reader (Setup's cleanup or selection, or an older host's `read_package`). Nothing native has shipped, so no installed or retained epoch is affected.
