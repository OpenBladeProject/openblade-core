[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$EtlPath,
    [Parameter(Mandatory)][string]$OutputPath,
    [ValidateRange(1, 2000000)][int]$MaximumEvents = 2000000,
    [ValidateRange(1, 300)][int]$TimeoutSeconds = 120
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Core' -or $PSVersionTable.PSVersion -lt [version]'7.4' -or -not $IsWindows -or
    [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne [Runtime.InteropServices.Architecture]::X64) {
    throw 'Offline ETL export requires x64 PowerShell 7.4 or later on Windows.'
}
if (-not ('OpenBlade.MailboxObservation.TraceExporter' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot 'MailboxTraceExporter.cs') -ReferencedAssemblies @(
        'System.Diagnostics.EventLog', 'System.Text.Json', 'System.Xml.ReaderWriter',
        'System.Xml.XDocument', 'System.Xml.Linq', 'System.Collections',
        'System.Security.Cryptography', 'System.Text.Encoding.Extensions'
    )
}
[OpenBlade.MailboxObservation.TraceExporter]::Export(
    $EtlPath, $OutputPath, $MaximumEvents, $TimeoutSeconds)
