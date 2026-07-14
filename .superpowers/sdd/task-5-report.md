# Task 5 Report: Shared HID Enumeration Seam and Exact Operational Handle

## Status

Implemented the shared Windows HID discovery/native seam and an internal opener that returns an operational handle only after exact host, guard, same-handle identity, and cardinality gates. No real HID enumeration, physical HID access, feature I/O, or composition/command wiring was exercised or added.

## RED evidence

Focused command:

```powershell
dotnet test tests\OpenSynapse.Protocol.Tests\OpenSynapse.Protocol.Tests.csproj --no-restore --filter 'FullyQualifiedName~WindowsRazerHidSessionOpenerTests|FullyQualifiedName~WindowsHidEnumeratorTests' --verbosity minimal
```

Initial meaningful RED exited 1. Both new test classes failed to compile because the requested Task 5 API was absent, including `IHidCandidateNative`, `HidHandleIdentity`, `HidOpenParameters`, `OpenedCandidateHandle`, `OpenedHidHandle`, and `WindowsRazerHidSessionOpener` (`CS0246`). This followed an isolated-worktree restore; no production code had been added before the failing tests.

The review-driven retained-candidate cleanup regression was additionally mutation-checked: temporarily removing `DisposeAll(matches)` from the native-exception path made `Open_IdentityFailureAfterExactMatchDisposesEveryOwnedCandidateExactlyOnce` fail because the retained handle remained open. Restoring the cleanup returned it to GREEN.

## GREEN evidence

Final focused command:

```powershell
dotnet test tests\OpenSynapse.Protocol.Tests\OpenSynapse.Protocol.Tests.csproj --no-restore --filter 'FullyQualifiedName~WindowsRazerHidSessionOpenerTests|FullyQualifiedName~WindowsHidEnumeratorTests' --verbosity minimal
```

Result: exit 0; 19 passed, 0 failed, 0 skipped.

Covered behavior:

- Exact host gate precedes guard and native discovery; host failure makes zero guard/enumeration/open calls.
- Guard conflict and detection failure precede enumeration/open and map to stable failure kinds.
- Every operational candidate is opened independently with the exact requested access/share/disposition/overlapped/inheritance parameters.
- Identity is read from the same retained candidate handle used for selection and binding.
- Every VID/PID/usage page/usage/feature-length mismatch is rejected.
- Zero and multiple exact matches are identity-blocked; ambiguous handles are disposed exactly once and never bound.
- Enumeration/open/read/bind exceptions map to `NativeIoFailure`; failing and previously retained handles are disposed exactly once.
- A sole exact handle is bound once only after all candidates have been evaluated.
- Operational disposal is idempotent and invokes bound-handle disposal before file-handle disposal.
- Diagnostic enumeration uses the injected shared seam, does not bind, retains metadata-only behavior, and disposes diagnostic candidates.

## Full-suite evidence

Command:

```powershell
dotnet test OpenSynapse.slnx --no-restore --verbosity minimal
```

Result: exit 0; 289 passed, 0 failed, 0 skipped across Protocol (154), Contracts (11), Setup (28), Capture (27), Tray (15), and Service (54).

## Ownership decisions

- `WindowsRazerHidSessionOpener` owns every successfully opened, unbound candidate.
- Rejected candidates are disposed immediately; exact candidates are retained only through cardinality determination.
- Any later native exception disposes both the currently failing candidate and all previously retained exact candidates.
- Multiple exact candidates are all disposed without binding.
- Only the sole exact candidate reaches `BindSelected`; `WindowsHidNative` binds its file handle, transfers candidate ownership exactly once, and returns the operational wrapper.
- If operational-wrapper construction were to fail after transfer, `WindowsHidNative` disposes both the bound handle and transferred file handle.
- `OpenedCandidateHandle.Dispose` and transfer are guarded by an atomic ownership flag.
- `OpenedHidHandle.DisposeAsync` is idempotent and disposes the `ThreadPoolBoundHandle` before the `SafeFileHandle`, including the file cleanup in a `finally` path.
- Tests use scripted native seams and fake/sentinel candidate resources. The sole binding/disposal tests use raw overlapped, delete-on-close temporary-file handles; they never enumerate or open HID hardware.

## Files

- Created `src/OpenSynapse.Protocol/HidHandleIdentity.cs`
- Created `src/OpenSynapse.Protocol/WindowsRazerHidSessionOpener.cs`
- Created `src/OpenSynapse.Protocol/WindowsHidNative.cs`
- Modified `src/OpenSynapse.Protocol/WindowsHidEnumerator.cs`
- Created `tests/OpenSynapse.Protocol.Tests/WindowsHidEnumeratorTests.cs`
- Created `tests/OpenSynapse.Protocol.Tests/WindowsRazerHidSessionOpenerTests.cs`
- Created `.superpowers/sdd/task-5-report.md`

## Self-review

- Confirmed all SetupAPI, HID metadata, and `CreateFileW` declarations formerly in the enumerator now exist only in `WindowsHidNative`.
- Confirmed the operational opener is not composed into Capture, Service, CLI, IPC, tray, or any command path.
- Confirmed no feature SET/GET, `DeviceIoControl`, cancellation, protocol command, fixture, or capability logic was introduced.
- Confirmed diagnostic interface selection remains `HidInterfaceInfo` metadata and cannot authorize an operational handle.
- Independent review found no critical issues. Its two important ownership/test findings were addressed: defensive post-transfer cleanup and regression coverage for a retained exact candidate followed by an identity-read failure.
- Remaining minor concern: `HidP_GetCaps` failures are currently wrapped with `Win32Exception(status)`, although the status is an NTSTATUS. This may make future diagnostic text less precise, but it does not escape through the stable opener result and does not affect ownership or authorization.
