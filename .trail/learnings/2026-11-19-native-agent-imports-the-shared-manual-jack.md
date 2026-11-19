---
title: "Native agent imports, the shared manual-write frame tests and obid headroom"
author: Jack
date: 2026-11-19
branch: feat/native-agent-color-profile
---

## Decided
- Real Agent images import mscms.dll in every display build (ColorProfileGetDisplayList/Default/UserScope for the query; the association, default and WcsSetUsePerUserProfiles setters only with setColorProfile) and webservices.dll with agent-battery-report-v1 (WsCreateReader/WsSetInput/WsReadNode/WsGetReaderNode/WsFreeReader over an in-memory buffer in session_battery/xml.rs). Both are inbox system DLLs and are allowlisted in WindowsNativePackage.IsSystemImport and native_package::SYSTEM_IMPORTS, each with a reason comment. RealAgentImports (C#) and REAL_AGENT_IMPORTS (Rust) pin the import set of a real bound all-features Agent.
- The two allowlists cannot drift. A Rust test reads WindowsNativePackage.cs with include_str!, requires the same names, and pins the whole managed API-set rule text after them, so nothing else can be admitted there. Both predicates also check the shared admit/refuse rows in tools/openblade-windows-host/tests/data/native-api-set-imports-v1.json (Rust directly, C# through NativeArtifact.Inspect). rust.yml also runs on changes to WindowsNativePackage.cs, so a managed-only edit cannot skip the check.
- The shared manual-write frame (manual_write::run: pause gate, checkpoint, power admission, reply reconciliation, staleness, fatal reply) is tested once in manual_write_tests.rs over a trivial request. The HDR, refresh and color test files keep only what their own attempt adds. The 12 frame mutants scripted first are killed by those tests alone, but that set was never a completeness statement. Review then found three survivors, now pinned: a refused request whose reply query fails fatally reported Blocked, and staleness compared only the power source or only the generation. A mutant count describes one review's scope.
- Test Suppliers stub the setters they do not script with unscripted_writes!(...). The stub panics, and the endpoint awaits the supplier inline, so an unexpected write fails the test at the call.
- ccd_write is now manual_write (ManualHost, ManualRequest, ManualWriteOutcome, cfg openblade_display_manual_write), because the frame also runs the WCS color write.

## Tried and ruled out
- Removing webservices.dll as an accidental link. It is the deliberate XML parser of the battery report, not a stray windows-sys feature.
- Admitting vcruntime140.dll. Only dynamic-CRT images import it, and neither list admits it. tools/openblade-windows-host/.cargo/config.toml sets +crt-static only when cargo runs from that crate directory; what guarantees a bound image has no CRT import is the build.rs assert that a nonce-bound x64 build has crt-static.

## Watch out
- The .obid identity record cap is 4096 bytes (it was 1024 when this was written; see 2026-11-19-native-build-identity-bound-and-the-jack.md). The largest shippable composition (service-temporary-charge-v1 plus every Agent feature, release profile) is 977 bytes for the agent role and 927 for the service role, so about 44 more Agent capabilities of about 70 bytes fit. Past the cap, build.rs asserts and managed Inspect refuses. Enabling every crate feature, fixtures included, is 1076.
- After a cfg rename (ccd_write to manual_write) the host build.rs output sits in the shared target C:/build-cache/cargo/openblade-core, which other checkouts build into too. A build from another checkout can leave the old cfg emission there, and the build then fails with compile errors on the renamed cfg-gated code. Build only this branch's HEAD in that target, and touch rust/crates/openblade-platform-windows/src/lib.rs before taking shared-crate test counts.
- No automated check runs a real, feature-enabled bound image through the C# or Rust inspector; the fixtures are synthetic PEs. A new DLL import therefore surfaces only at packaging, where both inspectors fail closed (managed: "Unreviewed external import contract.").
- A fixture PE with more than one import descriptor needs a larger .idata. WindowsNativeEpochTests.Image(imports:) switches to a 0x400-byte section and leaves the default single-KERNEL32 layout byte-identical; the Rust with_imports helper grows the vector image the same way.
