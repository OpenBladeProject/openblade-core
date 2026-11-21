---
title: Numeric JSON assertions and lane test scheduling guards
author: Jack
date: 2026-11-21
branch: test/ci-startup-guard-and-guid-flakes
cost: { minutes: 8, tokens: 114367 }
touches:
  - tests/OpenBlade.Capture.Tests/Rz090528PadlessLoadedHyperBoostValidationCommandTests.cs
  - tests/OpenBlade.Contracts.Tests/IpcMessageCodecTests.Protocol51.cs
---

## Decided
Protocol51Projects5500RpmIntentIntoFrozenNumericBounds asserted `DoesNotContain("5500", json)` on a frame that carries random GUIDs; one CI run hit a GUID ending in 5500. Assert numeric RPM absence by walking the JsonNode tree for number values (`NumericValues` helper in IpcMessageCodecTests.Protocol51.cs) and checking 5500 is absent and 5400 present. Do not substring-match digits against serialized JSON that contains IDs, timestamps or hashes.

In the Capture OwnedReadOnlyLane tests, the 2 s entered-signal wait and the post-release CloseAsync (which waits only the lane's own timeout, 200 ms in the blocked-read test) are scheduling guards, not the deadline under test. Use ten-second guards there; retry CloseAsync within the guard. AcceptedReadPublishesOnceAndClosesBeforeDisposalClaim now passes a five-minute fixture deadline because the production two-second default starts at queue time.

## Tried and ruled out
`Assert.ThrowsAsync<TimeoutException>(() => read.WaitAsync(guard))` passes vacuously when the guard itself times out; wait with Task.WhenAny first, then assert on the read. The production lane deadline is unchanged.

## Watch out
Other suites still substring-match digit literals (for example DiagnosticArchiveProviderTests "12345" against configuration JSON, Protocol tests matching hex write prefixes). Check whether the searched text can contain random IDs before trusting those. Local Capture builds need the native payload: pass `-p:OpenBladeNativeDevelopmentBuild=false -p:OpenBladeNativePayloadDirectory=<prebuilt obj/native/Debug>` when the Rust toolchain proxy is missing.
