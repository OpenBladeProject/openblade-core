# Phase 4b Windows configuration transaction inspection

Run the managed writer oracle and the Rust restart replay from the repository root.

```powershell
dotnet test tests/OpenBlade.Service.Tests/OpenBlade.Service.Tests.csproj --filter FullyQualifiedName~BatteryConfigurationStoreTests
cargo +1.99.1 test --locked --manifest-path tools/openblade-windows-host/Cargo.toml --test configuration_recovery
```

[configuration-aggregate.json](../tests/OpenBlade.Service.Tests/WindowsReplay/v1/configuration-aggregate.json) freezes the managed unversioned profile example, two commits as exact UTF-8 documents, and the schema 1 recovery record from the second commit. Only the record's random transaction ID and test root are normalized. The managed oracle checks the three ordered targets, paths and exact backups in a new temporary root. Rust replays the fixture through a fake store. The [inspection vectors](../tests/OpenBlade.Service.Tests/WindowsReplay/v1/configuration-inspection.json) run against both readers. They cover quoted schemas, duplicates, invalid escapes, malformed documents, and the managed depth boundary.

- The Windows service supplies the machine root and fixed names `profiles.json`, `battery-preferences.json` and `battery-configuration.commit.json`. The store resolves them to full paths and serializes callers sharing the same path tuple. The writer durably stages each target and saves an exact backup for each existing preimage. It exclusively creates and flushes the fixed `.recovery-required` record before the first replacement. The record carries full target paths, prior existence and nullable backup and restore paths. It carries no preimage or intended digests.
- The managed writer replaces profiles, then preferences, then the marker. The marker commits the two original document byte streams with uppercase SHA-256 and their shared schema. Readback verifies the three intended byte streams and validated values. In-process rollback uses the captured preimages to verify backups, restores the data files first and the marker last, then verifies exact restoration. Successful resolution removes the record before best-effort backup cleanup. If rollback or record cleanup fails, the sidecar persists.
- Restart does not reconstruct the lost preimages or resume replacement. Any entry at the fixed recovery path, including a directory, empty file or malformed or foreign record, refuses admission before reading the primary documents. The Rust inspector takes a typed observation of that entry and never follows paths from its contents. An unavailable read is distinct from absence. Without a record it distinguishes an empty store, an unversioned profile candidate, incomplete or invalid documents, future schema, marker digest mismatch and a profile schema mismatch.
- `CommittedEnvelope` means the transaction-level format and hashes match. The original payloads stay with the caller for schema-specific validation. The reader leaves profile and preference values untouched, including numeric enums. Full domain validation, legacy decoding, migration and default materialization remain managed. In cases with multiple independent defects, the envelope reason can precede a deeper managed payload error. The inspector reports unavailable storage as an explicit preserve outcome instead of carrying a filesystem exception. It also maps a quoted profile schema and lone surrogate property names to invalid documents. The managed reader can throw InvalidOperationException for these inputs. The forward-schema scan still recognizes numeric future schemas beside escaped strings that cannot be decoded.

The Rust library is in the separate developer workspace and has no filesystem adapter, CLI or IPC hook. Its decisions describe one caller-supplied coherent snapshot. They cannot authorize a subsequent write or activation. The future Windows owner still needs a trusted root and path/ACL admission, protection against reparse and snapshot races, exclusive writer ownership, durable stage and record creation, Windows replacement and flush semantics, exact readback and crash tests. A stale or unfamiliar record needs operator reconciliation from independently verified evidence. Matching primary hashes cannot clear it.

The next Phase 4 work includes profile and preference migration and bootstrap, power/session/controller inputs, serialized effect execution, durable configuration writes and specialty ownership journals. C# retains live configuration, runtime and hardware ownership.

## Phase 4d Windows writer replay

The Windows host builds the writer only in its library test binary. Each test creates a fresh, exclusively allocated directory under the Windows temporary path. There is no entry point that takes an existing configuration root. The adapter pins plain directory components without delete sharing, uses fixed filenames and generated transaction IDs, and rejects reparse entries and multiply linked files. An exclusive lock file coordinates writer and restart inspection calls in the harness. This design assumes a test-owned directory and checks for outside changes at transaction boundaries. Production use needs trusted-root and ACL admission and a Windows namespace design that closes races with noncooperating processes.

The writer consumes the three UTF-8 streams from an already validated configuration. It checks the envelope and exact uppercase marker digests with the shared inspector and accepts only the current schema for writing. It can write the first transaction into an empty test root or replace a complete current-schema envelope. Full profile and preference value validation, legacy migration, and production bootstrap stay outside this slice.

The adapter creates stage files and exact preimage backups without overwrite, uses write-through file handles, and calls `sync_all` before publishing the schema 1 recovery record. It creates that fixed record exclusively, flushes it, and holds its handle through replacement and rollback. It uses same-directory `MoveFileExW` with `MOVEFILE_WRITE_THROUGH` and enables replacement only when the corresponding preimage existed. Each replacement is followed by independent byte readback. The marker is last. Rollback checks the known submission state and exact backups before restoring changed targets in the same order. It verifies restored bytes or absence before clearing the sidecar by its own handle. A collision, unknown state after submission, failed restoration, or failed sidecar clear preserves recovery evidence. Restart refuses any sidecar entry without parsing it. Artifact cleanup errors are reported separately from the verified commit or rollback.

Run the native persistence suite and the managed oracle from the repository root on Windows with the pinned Rust toolchain on the path.

```powershell
$manifest = 'tools/openblade-windows-host/Cargo.toml'
cargo +1.99.1 test --locked --manifest-path $manifest --lib --test configuration_recovery
$artifacts = cargo +1.99.1 test --locked --manifest-path $manifest --lib --no-run --message-format=json
$env:OPENBLADE_RUST_CONFIG_TEST_EXE = ($artifacts | ConvertFrom-Json | Where-Object {
    $_.reason -eq 'compiler-artifact' -and $_.profile.test -and
        $_.target.name -eq 'openblade_windows_host' -and $_.executable
}).executable
dotnet test tests/OpenBlade.Service.Tests/OpenBlade.Service.Tests.csproj --filter FullyQualifiedName~BatteryConfigurationStoreTests
```

The managed oracle produces a fresh aggregate and sends its bytes to the Rust test over standard input. Rust checks those bytes against the pinned fixture, admits them from disk, writes the next commit, and sends its readback to the managed reader. The managed reader validates the aggregate and the 75 percent charge limit. The native tests interrupt the transaction before and after primary replacement and exercise real sharing and collision failures in temporary directories. These tests establish ordering, exact rollback, and restart quarantine under injected interruption. A simulated interruption does not establish disk or filesystem durability across power loss.

C# retains live ownership. Before cutover, wire typed domain validation and profile bootstrap and migration, admit the production root and its ACLs, coordinate ownership across processes, and integrate the separate specialty journals and operator recovery flow. Verify the Windows filesystem and storage behavior for the supported production environment.
