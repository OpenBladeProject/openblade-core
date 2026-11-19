---
title: "Native agent imports, the shared manual-write frame tests and obid headroom"
author: Jack
date: 2026-11-19
branch: feat/native-agent-color-profile
---

## Decided
- Real Agent images import mscms.dll in every display build (ColorProfileGetDisplayList/Default/UserScope for the query; the association, default and WcsSetUsePerUserProfiles setters only with setColorProfile) and webservices.dll with agent-battery-report-v1 (WsCreateReader/WsSetInput/WsReadNode/WsGetReaderNode/WsFreeReader over an in-memory buffer in session_battery/xml.rs). Both are inbox system DLLs and are allowlisted in WindowsNativePackage.IsSystemImport and native_package::system_import, each with a reason comment. RealAgentImports (C#) and REAL_AGENT_IMPORTS (Rust) pin the import set of a real bound all-features Agent.
- The shared manual-write frame (manual_write::run: pause gate, checkpoint, power admission, reply reconciliation, staleness, fatal reply) is tested once in manual_write_tests.rs over a trivial request. The HDR, refresh and color test files keep only what their own attempt adds. The frame mutants (12) are all killed by those tests alone.
- Test Suppliers stub the setters they do not script with unscripted_writes!(...). The stub panics, and the endpoint awaits the supplier inline, so an unexpected write fails the test at the call.
- ccd_write is now manual_write (ManualHost, ManualRequest, ManualWriteOutcome, cfg openblade_display_manual_write), because the frame also runs the WCS color write.

## Tried and ruled out
- Removing webservices.dll as an accidental link. It is the deliberate XML parser of the battery report, not a stray windows-sys feature.

## Watch out
- The .obid identity record cap is 1024 bytes. The largest shippable composition (service-temporary-charge-v1 plus every Agent feature, release profile) is 977 bytes for the agent role and 927 for the service role. The next Agent capability adds about 70 bytes and will exceed the cap: build.rs asserts and managed Inspect refuses. Enabling every crate feature, fixtures included, is already 1076.
- Debug dynamic-CRT images also import vcruntime140.dll. The Rust list admits it and the managed list does not. Bound builds use the static CRT and never import it.
- A fixture PE with more than one import descriptor needs a larger .idata. WindowsNativeEpochTests.Image(imports:) switches to a 0x400-byte section and leaves the default single-KERNEL32 layout byte-identical.
