# Windows Rust Phase 5o: manual SO6120 Fan Stop entry replay

This fixture-only library replays the decision to admit a new manual supervised
Stop request for RZ09-0581 / 1532:02E0 / SO6120 EC 1.04. It returns the first
refusal stage or a successful replay verdict. The Windows development host
does not call it from its service, IPC, configuration or hardware paths.

The Rust base is PR #936 at
`6b1de6d24970bad7063bebdbfe2b6f05e7aae2cc`.
The managed oracle is public main at
`1d2689d163a15c4b9566a3b6660fce68c0dbd078`, used as a separate source reference.

| Managed reference | Replay boundary |
| --- | --- |
| `ServiceControlRuntime.FanStop.cs:171–224,326–342` and `FanStopController.cs:511–543` | Manual dispatch, recovery/previous-owner fences, live service gates, resolved USB-C and a typed Balanced readback. The fixture requires actual readback evidence; the managed presentation fallback is insufficient evidence here. |
| `FanStopModelContract.cs`, `So6120ServiceAdmissionPolicies.cs:13–72` and `So6120ServiceDutyPlatform.cs:65–159,212–248` | Exact dual-registry host observation, X64, EC 1.04, exactly one control collection with the managed 91-byte geometry, pinned contract/firmware and driver, and the original owner's delegation and console checks. Firmware hash is reference provenance; SMBIOS does not attest the running hash. SKU and BIOS are not runtime gates in this managed service path. |
| `Program.cs:203–225` and `So6120ServiceDutyPlatform.cs:257–354,364–389` | Initial resolved USB-C, entry volatile evidence, typed power reuse for at most two seconds from query start, and a refreshed read on expiry or changed AC. At application, require current bound console and AC present plus fresh power and safety. |
| `So6120ServiceSafetySampler.cs:81–166` and `FanStopThermalPolicy.cs:23–37,82–113` | CPU-reader readiness is separate from sample completion. A completed same-run observation expires two seconds from the start of volatile admission. Auto requires a valid CPU at or below 55°C and an inactive GPU. Custom uses inclusive immediate cutoffs, and requires a valid GPU sample unless D3 is explicitly observed. Entry does not credit rolling averages. A prerequisite refusal cannot borrow thermal details from a previous observation. |
| `So6120OwnedStopSession.cs:21,47–65,173–185` | The entry window closes at 45 seconds, independently of the two-second evidence ages. The replay checks cancellation and the budget again at application. |

The fixture supplies the results of OS and service checks as typed observations,
including validated settings, registry identity, and journal/ownership matches.
It does not acquire those observations. The actual sampler's asynchronous
readiness, polling, timeouts, terminal latch across repeated native calls and
disposal remain unported. The cached gate is modeled once: a newer observation
counts only if it has completed in the same run before the supplied boundary;
an already refused attempt is not retried.

Remaining parity includes independent live device/driver and service ownership
acquisition, native per-command checks and timing, guardian process and
hardware-lease lifetime, durable reservation and recovery, command submission
and readback, stopped tachometer confirmation, owned continuation and power
transitions, and guarded Automatic cleanup. Battery Saver and automatic entry
are separate paths. This replay does not assert native, installed, or physical
parity.

To rerun the thermal and identity oracle, use a disposable checkout of the pinned
managed commit. Copy `ManualFanStopEntryOracle.cs` from the Windows host's
`tests/oracles/manual_fan_stop/` directory into
`tests/OpenBlade.Service.Tests/` in that checkout. Set
`OPENBLADE_FAN_STOP_REPLAY_CASES` and `OPENBLADE_FAN_STOP_REPLAY_PINS` to the
absolute paths of the TSV and pins fixtures, respectively. Run
`dotnet test tests/OpenBlade.Service.Tests --filter FullyQualifiedName~ManualFanStopEntryOracle`.
It calls the managed policy and diagnostic formatter directly. The Rust tests
consume the same fixtures and exercise the full typed replay and refusal order.

## Phase 5p worker-backed replay

The local integration composes Phase 5o
(`2b442608d49a6d06fce1bc1b05f940be5a5b540d`) with the Phase 5l private CPU
worker (`b4abf2424b1d0aae528ddd20c1ab4eed820c2afc`).
`fan_stop_entry::worker::replay_worker` shares the original context,
power, cancellation and first-refusal order. Its inputs keep readiness, CPU
temperature and scripted GPU state separate.

The supervisor issues a typed readiness value and a separately sequenced
sample. The replay accepts a sample only for its original attempt and volatile
observation, after readiness and volatile completion, and only if the reply
arrived by the safety snapshot's completion. It ages the reading from its
request and ages the whole proof from the start of volatile admission, with a
two-second limit for each. Replacement, retirement and loss of exit observation
invalidate worker evidence. A newer safety snapshot must carry its own sample.
Auto requires observed GPU inactivity. Custom accepts an observed in-range GPU
temperature or explicit D3.

The managed sampler supplies the ordering and age rules. Both Rust paths use
the shared thermal corpus. Worker-backed tests cover the corpus rows that fit
the worker wire and replay timing and use separate cases for missing, expired,
misordered, canceled and uncertain evidence. The native transport smoke
launches only the existing inert worker fixture. That smoke exercises process,
pipe and exit observation independently of the composed replay.

The composed verdict is scripted and carries no hardware authority. Live
sampling and protected installed-worker identity, exact native admission,
ownership, command submission and independent readback remain separate gates.
The local input commits and composed delta await exact external review.

Version impact: none. Product, persisted configuration and IPC versions are unchanged.

## Phase 5q guardian lifecycle replay

The local Windows development library starts at Phase 5p
`c67b2310bcb0ce51d5827efa77c9ff7fd729e08b`. Its managed oracle is
`1d2689d163a15c4b9566a3b6660fce68c0dbd078`; the guardian sources and focused
test files below are unchanged between that oracle and the Phase 5p base.

| Managed reference | Replay boundary |
| --- | --- |
| `GuardianFanStopBackend.cs` and `FanStopController.cs:461–673` | Exclusive delegation, operation identity, monotonic evidence, historical Stop confirmation, one logical Auto request, cancellation, and a sticky service recovery disposition. |
| `FanStopGuardianTransport.cs` and `WindowsFanStopGuardianClient.cs` | Durable reservation before dispatch, authenticated owner observations, exact owner exit and durable receipt equality, archive before lease release. The replay receives these as typed effects. |
| `FanStopGuardianClient.cs:10–36` | Automatic requires confirmed Auto and readback, cleanup, journal completion, native release and owner exit. A separately proven never-submitted refusal reports Ordinary and does not need Auto or cleanup evidence. |
| `ServiceControlRuntime.FanStop.cs:138–164` | The background pump consumes only cached continuation evidence. It selects a heartbeat or its fixed Automatic control, independently of the manual entry verdict. |

An admitted entry permits a scripted dispatch; it does not confirm that Stop
occurred. A matching fan value, owner exit alone, or a canceled waiter cannot
release the lease. A matching live terminal result needs independent exit and
durable confirmation. An uncertain reservation or dispatch, a lost channel, or
invalid evidence retains the fence. A service confirmation timeout still lets
an intact owner path finish and release the lease on full proof; the service
recovery disposition remains set. A new service instance uses startup
disposition and does not adopt a prior owner.

The Rust fixtures exercise these transitions with recorded effect calls.
Compare with the pinned `GuardianFanStopBackendTests`,
`FanStopGuardianStoreTests`, `FanStopGuardianTransportTests`,
`FanStopGuardianProgressTests`, `FanStopContinuationPolicyTests`, and the
`GuardianHeartbeatUsesOnlyCachedContinuationIncludingAfterReleaseAndLifecycleLoss`
fixture. Repeated Rust cache reads are harmless; duplicate live revisions are
rejected at the owner-observation boundary.

The Windows host does not call this library from a live service path. Remaining
gates include protected reservation and recovery, authenticated private
transport and the retained process handle, actual exclusion lease and owner
lifetime, and owner-side exact-device admission, timing, thermal continuation,
command submission, independent readback and native cleanup. The scripted
effects do not establish installed or physical behavior. Product, IPC and
persisted schema versions are unchanged.
