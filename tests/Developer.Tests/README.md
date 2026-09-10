# Developer integration tests

These tests validate retained developer tooling that is not shipped by the
production installer. They run in their own path-scoped workflow so an optional
developer SDK dependency cannot block an unrelated Stable or Nightly build.

`SparsePackageIdentity.Tests.ps1` requires the Windows SDK `MakeAppx.exe` and
`mt.exe` tools in the same SDK binary directory. A missing tool fails this
developer validation; it is not treated as a skipped or passing test.

Restore the Session Agent before running the test directly:

```powershell
dotnet restore .\src\OpenBlade.SessionAgent\OpenBlade.SessionAgent.csproj `
    --runtime win-x64 -p:NuGetAudit=false
.\tests\Developer.Tests\SparsePackageIdentity.Tests.ps1
```
