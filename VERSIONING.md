# Versioning

OpenBlade uses Semantic Versioning for product releases and separate monotonic
integers for persisted schemas and IPC protocols.

## Product version

`OpenBladeVersion` in `Directory.Build.props` is the single source of truth.
Release tags use `vMAJOR.MINOR.PATCH`.

- `MAJOR`: stable-release compatibility break. Before 1.0, reserve this for a
  deliberate project-wide compatibility reset.
- `MINOR`: a new user-facing capability, a newly supported Blade model, or a
  compatible expansion of public behavior. During 0.x development, an
  intentional breaking product change also increments `MINOR`.
- `PATCH`: a backward-compatible bug fix, performance improvement, packaging
  correction, or small presentation refinement.
- Documentation, tests, capture evidence, and internal refactors alone do not
  require a product-version bump.

Assembly identities are derived as follows:

- `AssemblyVersion`: `MAJOR.MINOR.PATCH.0`
- `FileVersion`: `MAJOR.MINOR.PATCH.0`
- `InformationalVersion`: `MAJOR.MINOR.PATCH+<commit>` when source revision
  metadata is available

The UI and service display their own built assembly versions. If a change does
not touch a component, preserve its installed binary instead of rebuilding it
merely to change the embedded commit hash. In particular, tray-only releases
must use the tray-only deployment path and leave the service and Session Agent
files and versions unchanged.

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
2. Decide the SemVer increment from the rules above.
3. Update `OpenBladeVersion`.
4. Move `Unreleased` entries under the dated release heading and create a new
   empty `Unreleased` section.
5. Run the full build, tests, publish, payload validation, and applicable live
   device acceptance checks.
6. Commit as `chore(release): prepare vX.Y.Z`, tag `vX.Y.Z`, then publish the
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
