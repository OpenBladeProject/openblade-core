# Windows writer fence rehearsal

From the checkout root in a non-elevated PowerShell, build the two inert children using the locally available .NET SDK and Rust dependencies.

```powershell
dotnet build tests/OpenBlade.WriterFence.Scratch -p:NuGetAudit=false -p:RestoreSources=.
cargo build --offline --locked --manifest-path tools/openblade-windows-host/Cargo.toml --features scratch-writer-fence --bin openblade-scratch-rust-writer

$managed = (Resolve-Path tests/OpenBlade.WriterFence.Scratch/bin/Debug/net10.0-windows/OpenBlade.WriterFence.Scratch.exe).Path
$rust = (Resolve-Path tools/openblade-windows-host/target/debug/openblade-scratch-rust-writer.exe).Path
& $managed rehearse $rust (Get-Location).Path
```

The coordinator creates a new directory under the supplied existing local NTFS parent. It passes the directory and the empty lock file's Windows volume serial and file index to each child. Both children keep the admitted directories and the same exclusively opened file pinned while holding the lease. On successful completion the coordinator removes its own scratch directory. A failed rehearsal retains the scratch evidence.

The standalone rehearsal checks contention, release, exact lease identity and abrupt child exit.

Run the signed synthetic Setup rehearsal with those same locally built children.

```powershell
$env:OPENBLADE_PHASE_8K_RETIREMENT = "1"
$env:OPENBLADE_PHASE_8K_MANAGED_WRITER = $managed
$env:OPENBLADE_PHASE_8K_RUST_WRITER = $rust
dotnet test tests/OpenBlade.Setup.Tests -p:NuGetAudit=false -p:RestoreSources=. --filter "FullyQualifiedName~BundleUpgrade_FencesTheIncomingWriterUntilRetirementIsProved"
```

The test trusts the locally built fixtures supplied in these two absolute paths. It seals the fixture files, launches the inert child with private redirected stdio, and binds the exact terminal frame to the original Windows process and to the synthetic installed generation. It checks the same lease file before and after receipt and requires clean exit on the original handle, fake SCM STOP and clear durable recovery state before handing off. The children have no queued configuration work beyond the lease protocol. The fixture channel authenticates the launched child within the rehearsal. Production service authentication, protected data ACLs, and service activation are outside this test.
