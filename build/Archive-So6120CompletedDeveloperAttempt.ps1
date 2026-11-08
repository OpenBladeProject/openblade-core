#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $EvidenceDirectory,
    [switch] $Archive
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'So6120CompletedAttemptArchive.ps1')
Invoke-So6120CompletedAttemptArchive -EvidenceDirectory $EvidenceDirectory -Archive:$Archive
