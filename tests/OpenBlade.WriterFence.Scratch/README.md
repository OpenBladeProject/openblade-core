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

The rehearsal exercises the existing scratch retirement policy with real child exit and generation receipts and a simulated SCM observation. The fixture does not use the installed managed repository, protected data ACLs, or service activation.
