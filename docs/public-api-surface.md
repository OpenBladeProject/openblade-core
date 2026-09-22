# Public API surface audit

This document records the audit performed before accepting the public API
baselines for `OpenBlade.Contracts`, `OpenBlade.Session`, and
`OpenBlade.Protocol`. The baselines are compatibility gates, so an entry should
represent an intentional assembly boundary and exclude incidental
accessibility.

## Method

The audit used the generated baseline as the complete inventory, then:

1. searched production projects for consumers outside the declaring assembly;
2. accounted for the existing `InternalsVisibleTo` boundary in Protocol;
3. treated explicit assertions in `PublicProtocolSurfaceTests` as existing API
   decisions, not authority for this audit to overturn;
4. trialled `internal` accessibility for apparent candidates; and
5. built the declaring project with warnings as errors so compiler diagnostics
   (`CS0050`, `CS0051`, and `CS0053`) exposed public-signature dependencies.

Public symbols were classified as:

- contract surface by the assembly's purpose, including IPC, serialized
  profiles, and typed protocol contracts;
- implementation surface required across an assembly boundary, explicitly
  pinned by a surface test, or required to close a public signature; or
- incidental surface with no production consumer outside an existing friend
  boundary and no public-signature dependency.

The compiler trial is a lower-bound audit. It proves when a public declaration
requires another public type, but it does not by itself prove that the
declaration is a desirable long-term API.

## Results

| Assembly | Exported types before audit | Narrowed types | Result |
|---|---:|---:|---|
| `OpenBlade.Contracts` | 110 | 0 | All exports remain contract surface. |
| `OpenBlade.Session` | 24 | 0 | All exports remain contracts or required cross-assembly implementation surface. |
| `OpenBlade.Protocol` | 63 | 2 | Two Windows HID discovery implementation types became internal. |

### OpenBlade.Contracts

No type was narrowed. The only exports without a direct production reference
outside Contracts were `LightingEffectParameters` and
`OffLightingParameters`. Both remain public because they are members of the
serialized, polymorphic lighting-profile contract hierarchy. The apparent lack
of a static reference is a consequence of serialization, not evidence that
they are private implementation details.

### OpenBlade.Session

No type was narrowed and no new friend assembly was added. A compiler trial
confirmed that the apparent candidates are part of existing public signatures:

- `DisplayPowerContext` is returned by `DisplayPowerContextState.Capture`;
- `IDisplayPowerContextSource` is accepted by `DisplayProfileApplier`;
- `DisplayAutomationObservation` is returned by observation methods; and
- `DisplayTransitionResult` is exposed by `DisplayAutomationCoordinator`.

The remaining concrete coordination and persistence types are used across
assemblies by production consumers in `OpenBlade.SessionAgent`.

### OpenBlade.Protocol

`HidInterfaceInfo` and `WindowsHidEnumerator` had no production consumers
outside Protocol's existing friend boundary. They are Windows HID discovery
implementation details and are now internal.

`TargetBladeDevice.IsExactInteractiveValidationTarget` accepted
`HidInterfaceInfo`, had no consumer outside that same boundary, and was
narrowed with it. The public `TargetBladeDevice` contract and its other public
members are unchanged.

The other apparent candidates were retained after the compiler trial showed
that they close existing public signatures or after existing surface tests
identified them as deliberate public API. This includes the temporary-charge
ports, protection-off validation port, validation result and target types,
lighting and fan readings, HID units, and Windows service snapshots. The
explicit Protocol surface assertions remain authoritative; changing those
decisions requires a separate API proposal outside this mechanical audit.

## Maintaining the baselines

After an intentional API change, regenerate all three manifests from fresh
build outputs:

```powershell
build\Update-PublicApiBaselines.ps1
```

Review the manifest diff as part of the change. Do not infer the current surface
from stale `bin` output or accept a baseline update without explaining every
addition, removal, and `abstract`/`virtual` change.
