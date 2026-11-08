#Requires -Version 7.2
# Offline, explicit developer bookkeeping. No native driver or hardware API.
# Pins identify the independently reviewed complete run documented in
# docs/so6120-guardian-caller-loss-validation.md; they admit no other attempt.
Set-StrictMode -Version 3.0

function Assert-So6120ArchiveSecurity([Security.AccessControl.FileSystemSecurity] $Security) {
    $trusted = @('S-1-5-18', 'S-1-5-32-544',
        'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
    if ($Security.GetOwner([Security.Principal.SecurityIdentifier]).Value -cnotin $trusted) {
        throw 'Developer marker storage owner is not trusted.'
    }
    $mutation = [Security.AccessControl.FileSystemRights]::Write -bor
        [Security.AccessControl.FileSystemRights]::Delete -bor
        [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
        [Security.AccessControl.FileSystemRights]::ChangePermissions -bor
        [Security.AccessControl.FileSystemRights]::TakeOwnership
    foreach ($rule in $Security.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
            ($rule.FileSystemRights -band $mutation) -ne 0 -and $rule.IdentityReference.Value -cnotin $trusted) {
            throw 'Developer marker storage is writable by an untrusted principal.'
        }
    }
}

function Assert-So6120MachineArchiveStorage([string] $Directory) {
    $programData = [Environment]::GetFolderPath('CommonApplicationData')
    $expected = Join-Path $programData 'OpenBlade\DeveloperValidation\SO6120'
    if ($Directory -cne $expected) { throw 'Only the fixed machine developer directory is admitted.' }
    Assert-So6120ArchivePath $Directory
    # ProgramData itself permits creating unrelated children. Every existing
    # directory below it in our fixed path must prevent untrusted replacement.
    for ($cursor = $Directory; $cursor -cne $programData; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
        Assert-So6120ArchiveSecurity (Get-Acl -LiteralPath $cursor -ErrorAction Stop)
    }
    $marker = Join-Path $Directory 'attempt.json'
    Assert-So6120ArchivePath $marker
    Assert-So6120ArchiveSecurity (Get-Acl -LiteralPath $marker -ErrorAction Stop)
}

function Assert-So6120ArchivePath([string] $Path, [switch] $AllowMissingLeaf) {
    $full = [IO.Path]::GetFullPath($Path)
    if ($full -cne $Path -or $full -notmatch '^[A-Za-z]:\\' -or $full.Substring(2).Contains(':')) {
        throw 'Archive paths must be canonical absolute local paths.'
    }
    $cursor = $full
    $leaf = $true
    while ($cursor) {
        if (-not (Test-Path -LiteralPath $cursor)) {
            if (-not ($leaf -and $AllowMissingLeaf)) { throw "Missing archive path: $cursor" }
        }
        elseif ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Reparse points are not admitted: $cursor"
        }
        $leaf = $false
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
}

function Get-So6120ArchiveHash([IO.Stream] $Stream) {
    $Stream.Position = 0
    try { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Stream)) }
    finally { $Stream.Position = 0 }
}

function Read-So6120ArchiveText([IO.Stream] $Stream) {
    $Stream.Position = 0
    $reader = [IO.StreamReader]::new($Stream, [Text.UTF8Encoding]::new($false, $true), $false, 4096, $true)
    try { $reader.ReadToEnd() }
    finally { $reader.Dispose(); $Stream.Position = 0 }
}

function Assert-So6120ArchiveExitEvidence($Capture, $Parent, $Closed, [string] $Run) {
    if ($Run -cne 'bc53359d-f19a-4de7-bab6-1792a637d1e3' -or
        $Capture.schemaVersion -ne 1 -or $Capture.observedChildExit -ne $true -or
        $Capture.observedOwnerExit -ne $true -or $Capture.callerLossAutoVerified -ne $true -or
        $Capture.ownerExitCode -ne 2 -or $null -ne $Capture.monitorError -or
        $Parent.observedWrapperExit -ne $true -or $Parent.wrapperExitCode -ne 0 -or
        $Parent.wrapperPid -ne $Capture.wrapperPid -or $null -ne $Parent.workerFailure -or
        $null -ne $Parent.workerFailureReceiptError -or $null -ne $Parent.captureReceiptError -or
        $Closed.cleanupConfirmed -ne $true -or $Closed.retainsNativeOwnership -ne $false -or
        $Closed.close.State -ne 1 -or $Closed.close.NativeError -ne 0 -or
        @($Closed.releaseFailures).Count -ne 0) {
        throw 'Independent Auto, native close and process-exit evidence is incomplete.'
    }
}

function Assert-So6120ArchivedProcessExited([int] $ProcessId, [DateTimeOffset] $ObservedExitAt) {
    if ($ProcessId -le 0) { throw 'Invalid original process identity.' }
    try { $process = [Diagnostics.Process]::GetProcessById($ProcessId) }
    catch [ArgumentException] { return }
    try {
        # A reused PID is safe only when its creation is later than the pinned
        # independent exit observation. Access errors remain blocking.
        if ($process.StartTime.ToUniversalTime() -le $ObservedExitAt.UtcDateTime) {
            throw "Original process $ProcessId may still be alive."
        }
    }
    finally { $process.Dispose() }
}

function Move-So6120VerifiedAttemptMarker([string] $Directory, [string] $Run) {
    Assert-So6120ArchivePath $Directory
    $marker = Join-Path $Directory 'attempt.json'
    $destination = Join-Path $Directory "attempt-$Run-completed.json"
    Assert-So6120ArchivePath $marker
    Assert-So6120ArchivePath $destination -AllowMissingLeaf
    if (Test-Path -LiteralPath $destination) { throw 'The archive destination already exists.' }
    $stream = [IO.FileStream]::new($marker, [IO.FileMode]::Open, [IO.FileAccess]::Read,
        ([IO.FileShare]::Read -bor [IO.FileShare]::Delete))
    try {
        Assert-So6120ArchivePath $marker
        if ($stream.Length -le 0 -or $stream.Length -gt 4096) { throw 'Invalid developer marker length.' }
        $record = Read-So6120ArchiveText $stream | ConvertFrom-Json
        if ($record.runId -cne $Run) { throw 'The current developer marker belongs to a different run.' }
        $hash = Get-So6120ArchiveHash $stream
        # The caller retains the global owner semaphore. Rename preserves the
        # original bytes; no marker is deleted, reset or overwritten.
        [IO.File]::Move($marker, $destination, $false)
        Assert-So6120ArchivePath $destination
        if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -cne $hash) {
            throw 'Archive verification failed; preserve both paths for inspection.'
        }
        [pscustomobject]@{ disposition = 'Archived'; run = $Run; markerSha256 = $hash; path = $destination }
    }
    finally { $stream.Dispose() }
}

function Invoke-So6120CompletedAttemptArchive([string] $EvidenceDirectory, [switch] $Archive) {
    $ErrorActionPreference = 'Stop'
    Assert-So6120ArchivePath $EvidenceDirectory
    $run = 'bc53359d-f19a-4de7-bab6-1792a637d1e3'
    $pins = [ordered]@{
        'attended-attempt\so6120-direct-duty.journal' = 'ACA7B486BED5AB9719785EDB1B6D4C6CDE23F11501E618C51C707F55723D1D5D'
        'attended-attempt\attended-run.jsonl' = '14E2AA891E2BFA3A18B7B2B2C059F1CBA47EA91122727CC921892672B8BBC9F9'
        'capture-result.json' = 'C8BAF41FA34233F421F23B1252149FBBD0C5A89EA7D59579A31F3A8E848C5597'
        'parent-result.json' = '8DF2C7BFE1670540EB704D0FF7A10966729D44AEC7829392C45C2DD6809F3B0E'
    }
    $locks = [Collections.Generic.List[IO.FileStream]]::new()
    $texts = @{}
    $guard = $null
    $held = $false
    try {
        foreach ($pin in $pins.GetEnumerator()) {
            $path = Join-Path $EvidenceDirectory $pin.Key
            Assert-So6120ArchivePath $path
            $stream = [IO.FileStream]::new($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
            $locks.Add($stream)
            Assert-So6120ArchivePath $path
            if ($stream.Length -le 0 -or $stream.Length -gt 8388608 -or
                (Get-So6120ArchiveHash $stream) -cne $pin.Value) { throw "Reviewed evidence hash differs: $($pin.Key)" }
            if (-not $pin.Key.EndsWith('.journal')) { $texts[$pin.Key] = Read-So6120ArchiveText $stream }
        }
        $capture = $texts['capture-result.json'] | ConvertFrom-Json
        $parent = $texts['parent-result.json'] | ConvertFrom-Json
        $rows = @($texts['attended-attempt\attended-run.jsonl'] -split '\r?\n' |
            Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json })
        $closed = @($rows | Where-Object { $_.phase -ceq 'closed' })
        if ($rows[0].run -cne $run -or $closed.Count -ne 1 -or $rows[-1].phase -cne 'completion') {
            throw 'The pinned completion log does not bind the reviewed run.'
        }
        Assert-So6120ArchiveExitEvidence $capture $parent $closed[0] $run
        if (-not $Archive) {
            return [pscustomobject]@{ disposition = 'VerifiedOnly'; run = $run; markerAccessed = $false }
        }
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        try {
            if (-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole(
                    [Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Explicit archival requires an administrator console.' }
        }
        finally { $identity.Dispose() }
        $guard = [Threading.Semaphore]::new(1, 1, 'Global\OpenBlade.SO6120.SupervisedDirectDuty')
        $held = $guard.WaitOne(0)
        if (-not $held) { throw 'A SO6120 owner is active; nothing was archived.' }
        $captureExit = [DateTimeOffset]::Parse($capture.observedAtUtc, [Globalization.CultureInfo]::InvariantCulture)
        $parentExit = [DateTimeOffset]::Parse($parent.observedAtUtc, [Globalization.CultureInfo]::InvariantCulture)
        Assert-So6120ArchivedProcessExited $capture.childPid $captureExit
        Assert-So6120ArchivedProcessExited $capture.ownerPid $captureExit
        Assert-So6120ArchivedProcessExited $capture.wrapperPid $parentExit
        $directory = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'OpenBlade\DeveloperValidation\SO6120'
        Assert-So6120MachineArchiveStorage $directory
        Move-So6120VerifiedAttemptMarker $directory $run
    }
    finally {
        if ($held) { [void]$guard.Release() }
        if ($null -ne $guard) { $guard.Dispose() }
        foreach ($stream in $locks) { $stream.Dispose() }
    }
}
