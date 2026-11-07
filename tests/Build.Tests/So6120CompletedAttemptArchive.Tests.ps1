#Requires -Version 7.2
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $repository 'build/So6120CompletedAttemptArchive.ps1')
$root = Join-Path ([IO.Path]::GetTempPath()) ('OpenBlade.ArchiveTest.' + [Guid]::NewGuid().ToString('N'))
$run = 'bc53359d-f19a-4de7-bab6-1792a637d1e3'
function Assert-Rejected([scriptblock] $Action, [string] $Message) {
    $caught = $false
    try { & $Action | Out-Null } catch { $caught = $true }
    if (-not $caught) { throw $Message }
}
try {
    [void][IO.Directory]::CreateDirectory($root)
    $capture = [pscustomobject]@{ schemaVersion=1; observedChildExit=$true; observedOwnerExit=$true;
        callerLossAutoVerified=$true; ownerExitCode=2; monitorError=$null; wrapperPid=123 }
    $parent = [pscustomobject]@{ observedWrapperExit=$true; wrapperExitCode=0; wrapperPid=123;
        workerFailure=$null; workerFailureReceiptError=$null; captureReceiptError=$null }
    $closed = [pscustomobject]@{ cleanupConfirmed=$true; retainsNativeOwnership=$false;
        close=[pscustomobject]@{ State=1; NativeError=0 }; releaseFailures=@() }
    Assert-So6120ArchiveExitEvidence $capture $parent $closed $run
    $closed.retainsNativeOwnership = $true
    Assert-Rejected { Assert-So6120ArchiveExitEvidence $capture $parent $closed $run } 'Retained native owner accepted.'
    $closed.retainsNativeOwnership = $false
    $closed.close.NativeError = 5
    Assert-Rejected { Assert-So6120ArchiveExitEvidence $capture $parent $closed $run } 'Native close failure accepted.'
    $closed.close.NativeError = 0
    $capture.observedOwnerExit = $false
    Assert-Rejected { Assert-So6120ArchiveExitEvidence $capture $parent $closed $run } 'Missing owner exit accepted.'
    $capture.observedOwnerExit = $true
    $parent.wrapperPid = 456
    Assert-Rejected { Assert-So6120ArchiveExitEvidence $capture $parent $closed $run } 'Unrelated parent receipt accepted.'
    Assert-Rejected { Assert-So6120ArchivedProcessExited $PID ([DateTimeOffset]::UtcNow) } 'Live original PID accepted.'
    Assert-Rejected { Assert-So6120ArchivePath (Join-Path $root '..\escape') } 'Noncanonical path accepted.'

    # Exercise ACL policy entirely in memory, without touching machine ACLs.
    $security = [Security.AccessControl.DirectorySecurity]::new()
    $system = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $everyone = [Security.Principal.SecurityIdentifier]::new('S-1-1-0')
    $security.SetOwner($system)
    $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($everyone,
        [Security.AccessControl.FileSystemRights]::ReadAndExecute, [Security.AccessControl.AccessControlType]::Allow))
    Assert-So6120ArchiveSecurity $security
    $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($everyone,
        [Security.AccessControl.FileSystemRights]::Write, [Security.AccessControl.AccessControlType]::Allow))
    Assert-Rejected { Assert-So6120ArchiveSecurity $security } 'Untrusted write ACL accepted.'
    $security = [Security.AccessControl.DirectorySecurity]::new()
    $security.SetOwner($everyone)
    Assert-Rejected { Assert-So6120ArchiveSecurity $security } 'Untrusted storage owner accepted.'
    Assert-Rejected { Assert-So6120MachineArchiveStorage $root } 'Non-machine path accepted by protected storage gate.'

    $marker = Join-Path $root 'attempt.json'
    $destination = Join-Path $root "attempt-$run-completed.json"
    [IO.File]::WriteAllText($marker, '{"runId":"another-run"}')
    Assert-Rejected { Move-So6120VerifiedAttemptMarker $root $run } 'Different attempt was archived.'
    if (-not (Test-Path -LiteralPath $marker)) { throw 'Rejected marker was changed.' }
    [IO.File]::WriteAllText($marker, ('{"runId":"' + $run + '","preserve":"original"}'))
    $original = [IO.File]::ReadAllBytes($marker)
    [IO.File]::WriteAllText($destination, 'existing archive')
    Assert-Rejected { Move-So6120VerifiedAttemptMarker $root $run } 'Existing archive was overwritten.'
    if ([IO.File]::ReadAllText($destination) -cne 'existing archive') { throw 'Existing archive was changed.' }
    [IO.File]::Delete($destination)
    $result = Move-So6120VerifiedAttemptMarker $root $run
    if ($result.disposition -cne 'Archived' -or (Test-Path -LiteralPath $marker) -or
        [Convert]::ToHexString($original) -cne [Convert]::ToHexString([IO.File]::ReadAllBytes($destination))) {
        throw 'Verified rename did not preserve exact marker bytes.'
    }
    Write-Output 'SO6120 offline archive tests passed; only temporary fixture markers were changed.'
}
finally {
    $resolved = [IO.Path]::GetFullPath($root)
    $prefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\OpenBlade.ArchiveTest.'
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected temporary root.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
