# Versioning

OpenBlade uses Semantic Versioning for product releases and separate monotonic
integers for persisted schemas and IPC protocols.

## Product version

`OpenBladeVersion` in `Directory.Build.props` is the single source of truth.
Release tags use `vMAJOR.MINOR.PATCH`.

For a current version `X.Y.Z`, increment the leftmost component justified by
the change and reset every component to its right to zero:

- `MAJOR` (`X`): a stable-release compatibility break. This includes removing
  or incompatibly changing a supported capability, persisted behavior, upgrade
  path, or user-facing contract so existing users cannot upgrade without
  intervention or an accepted loss of behavior. Moving from `0.x` to `1.0.0`
  declares the initial stable product contract. During `0.x` development,
  breaking changes increment `MINOR`; otherwise reserve `MAJOR` for `1.0.0` or
  a deliberate project-wide compatibility reset.
- `MINOR` (`Y`): a backward-compatible user-facing capability, newly supported
  Blade model, or meaningful compatible expansion of existing behavior. During
  `0.x` development, an intentional breaking product change also increments
  `MINOR`.
- `PATCH` (`Z`): a backward-compatible defect or security fix, measured
  performance improvement, packaging correction, reliability improvement, or
  small presentation refinement that does not add a distinct capability.
- `NONE`: documentation, tests, capture evidence, CI changes, dependency
  maintenance, and behavior-preserving internal refactors alone do not require
  a product-version bump.

Examples:

- `1.4.2` becomes `2.0.0` when a stable supported contract is broken.
- `1.4.2` becomes `1.5.0` when a backward-compatible capability is added.
- `1.4.2` becomes `1.4.3` when existing behavior is corrected or refined.
- `0.4.2` becomes `0.5.0` for either a new capability or an intentional
  pre-1.0 breaking change.

### Pull request version impact

Every pull request must include a `Version impact` field set to `major`,
`minor`, `patch`, or `none`, a `Version change` field containing the exact old
and new versions, and a one-sentence justification. Use `unchanged` for the
version change only when the impact is `none`. Apply these rules consistently:

1. Classify the user-visible and compatibility impact, not the commit type,
   number of files, or size of the diff.
2. For a pull request with several kinds of changes, declare the highest impact:
   `major` over `minor`, `minor` over `patch`, and `patch` over `none`.
3. Product SemVer, service IPC versions, Session Agent IPC versions, and
   persisted schema versions are separate decisions. An IPC or schema change
   does not automatically determine Product SemVer, but any resulting break in
   the supported user upgrade path does.
4. A pull request classified as `major`, `minor`, or `patch` must update
   `OpenBladeVersion` in `Directory.Build.props` in that same pull request. A
   pull request classified as `none` must leave it unchanged.
5. Calculate the new version from the current target-branch version. Before the
   pull request is marked ready or merged, refresh it from `main` and recalculate
   the bump if another merged pull request advanced the version. Never keep a
   stale or duplicate version merely to avoid resolving the version-line
   conflict.
6. Reset components to the right of the selected bump. For example, a `minor`
   change from `0.4.7` becomes `0.5.0`, while a `patch` change rebased after that
   merge becomes `0.5.1`.
7. A release-preparation pull request does not add another automatic bump. It
   releases the version already established by merged product changes unless
   the release pull request itself contains a change that independently
   requires a bump.
8. If the impact is uncertain, explain the ambiguity in the pull request and
   resolve it during review instead of silently defaulting to `patch` or `none`.

Use this form in the pull request body:

```text
Version impact: minor
Version change: 0.4.7 -> 0.5.0
Reason: Adds a backward-compatible user-facing capability.
```

Assembly identities are derived as follows:

- `AssemblyVersion`: `MAJOR.MINOR.PATCH.0`
- `FileVersion`: `MAJOR.MINOR.PATCH.0`
- `InformationalVersion`: `MAJOR.MINOR.PATCH+<commit>` when source revision
  metadata is available

The UI and service display their own built assembly versions. If a change does
not touch a component, preserve its installed binary instead of rebuilding it
merely to change the embedded commit hash. In particular, tray-only releases
must use the tray-only deployment path and leave the service and Session Agent
files and versions unchanged. The tray-only path is valid only when shared
Contracts and both IPC protocol versions are unchanged; otherwise publish and
install all coordinated components together.

### Nightly builds

Nightly builds retain the checked-in three-part `OpenBladeVersion` as their
stable base. The dedicated nightly workflow adds its monotonic run number as a
fourth numeric component for the Burn bundle, assembly, file, and installed
ownership versions. For example, nightly run 42 from base `0.1.0` uses binary
version `0.1.0.42` and informational version
`0.1.0-nightly.42+<commit>`. The next stable `0.1.1` normalizes to
`0.1.1.0` and therefore upgrades every `0.1.0.*` nightly.

Do not edit `OpenBladeVersion` merely to produce a nightly. Nightly identity is
supplied only by `build/Publish.ps1`, and revision zero remains reserved for
stable builds. The full Git revision, installer hash, and generated identity
must accompany every retained nightly artifact.

Every published nightly includes `OpenBlade-Nightly-CHANGELOG.md`, generated
from the exact tagged source history. A separate workflow job reconstructs any
nightly sections not yet recorded in the repository, rebases the result on the
current `main` changelog, leaves post-build entries under `Unreleased`, and
opens or updates the workflow-owned draft documentation pull request. The job
may replace only its dedicated branch with a force-with-lease guard; it never
pushes directly to `main`.

## Service IPC

The service IPC protocol version is defined in `IpcMessageCodec`.

- Increment it for a request/response discriminator change, required-field
  change, removed operation, or any change older peers cannot safely parse.
- An optional property with a safe legacy default may remain on the current
  version only when tests prove both omitted-property and new-property behavior.
- Reject unknown versions. Never guess or partially execute an unknown message.
- Update client, server, codec validation, tests, and diagnostics together.

The Session Agent IPC has its own version in `SessionDisplayIpc`. Apply the same
rules independently.

## Persisted configuration

Machine profiles and state use an explicit schema version. Every schema change
must have:

1. a new monotonic version;
2. a schema-specific reader or migration from every supported older version;
3. validation after migration;
4. atomic persistence only after successful validation; and
5. tests for old, current, malformed, and future versions.

Never silently reinterpret an old field. Preserve unknown or unset behavior as
non-destructive `Not set` semantics where applicable.

Per-user UI preferences are additive JSON. New optional fields require a safe
default and normalization that repairs missing, duplicate, or invalid values.
Introduce an explicit preference schema before making a non-additive change.

Exported profiles use their own `schemaVersion`. Imports dispatch to the exact
schema reader and reject future versions with a clear error.

## Release process

1. Keep user-visible work in `CHANGELOG.md` under `Unreleased`.
2. Confirm every merged product-changing pull request applied its required
   SemVer increment and that `OpenBladeVersion` is greater than the preceding
   release tag.
3. Move `Unreleased` entries under the dated release heading and create a new
   empty `Unreleased` section.
4. Run the full build, tests, publish, payload validation, and applicable live
   device acceptance checks.
5. Commit as `chore(release): prepare vX.Y.Z`, tag `vX.Y.Z`, then publish the
   immutable artifacts built from that tag.

## Commit messages

Use Conventional Commits:

```text
type(optional-scope): imperative summary

optional body explaining why and important constraints
```

Allowed types:

- `feat`: user-visible capability
- `fix`: defect correction
- `perf`: measured performance improvement
- `refactor`: behavior-preserving code restructure
- `docs`: documentation only
- `test`: test-only change
- `build`: build or packaging logic
- `ci`: continuous integration
- `chore`: maintenance, capture evidence, or dependency work
- `revert`: revert of an earlier commit

Common scopes are `tray`, `service`, `protocol`, `session`, `setup`, `capture`,
and `release`. Scopes are optional and should identify a real boundary, not a
ticket number.

Write the summary in lowercase imperative form, without a trailing period.
Keep it concise and put reasoning, risks, and migration detail in the body.
Use `BREAKING CHANGE:` in the footer only when the product-version rules also
classify the change as breaking.
