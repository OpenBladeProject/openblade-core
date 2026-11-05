$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Run the offline exporter fixtures on Windows PowerShell 7.' }
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
Add-Type -Path (Join-Path $root 'MailboxTraceExporter.cs') -ReferencedAssemblies @(
    'System.Diagnostics.EventLog', 'System.Text.Json', 'System.Xml.ReaderWriter',
    'System.Xml.XDocument', 'System.Xml.Linq', 'System.Collections',
    'System.Security.Cryptography', 'System.Text.Encoding.Extensions'
)
$script:checks = 0
function Assert-True([bool]$Value, [string]$Message) {
    if (-not $Value) { throw $Message }
    $script:checks++
}
function Assert-Throws([scriptblock]$Body, [string]$Message) {
    $thrown = $false
    try { & $Body } catch { $thrown = $true }
    Assert-True $thrown $Message
}
$prefix = @'
<Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event"><System>
<Provider Guid="{89497f50-effe-4440-8cf2-ce6b1cdcaca7}"/><EventID>0</EventID>
<Version>2</Version><Opcode>39</Opcode><TimeCreated SystemTime="2026-01-01T00:00:00Z"/>
<Execution ProcessID="4294967295" ThreadID="4294967295"/><EventRecordID>1</EventRecordID>
</System>
'@
$raw = $prefix + '<ProcessingErrorData><ErrorCode>15003</ErrorCode><EventPayload>0100</EventPayload></ProcessingErrorData></Event>'
$record = [OpenBlade.MailboxObservation.TraceExporter]::ParseEventXml($raw)
Assert-True ($record['payload_hex'] -eq '0100' -and $record['render_error'] -eq 15003) 'Raw undecoded payload was lost.'
Assert-True ($record['pid'] -eq 4294967295 -and $record['version'] -eq 2) 'Header identity changed.'
$unsupported = [OpenBlade.MailboxObservation.TraceExporter]::ParseEventXml(
    $prefix + '<EventData><Binary>0100</Binary></EventData></Event>')
Assert-True $unsupported['unsupported_payload_form'] 'Unsupported Binary payload looked decoded.'
$unsupported = [OpenBlade.MailboxObservation.TraceExporter]::ParseEventXml(
    $prefix + '<UserData><Unknown/></UserData></Event>')
Assert-True $unsupported['unsupported_payload_form'] 'Unsupported UserData looked decoded.'
Assert-Throws { [OpenBlade.MailboxObservation.TraceExporter]::ParseEventXml($raw.Replace('0100','xyz')) } 'Malformed hex accepted.'
Assert-Throws { [OpenBlade.MailboxObservation.TraceExporter]::ParseEventXml('<!DOCTYPE Event [<!ENTITY x "bad">]>' + $raw) } 'DTD accepted.'
Assert-Throws { [OpenBlade.MailboxObservation.TraceExporter]::ParseEventXml($prefix + '<EventData><Data Name="x">1</Data><Data Name="x">2</Data></EventData></Event>') } 'Duplicate fields accepted.'
Assert-Throws { [OpenBlade.MailboxObservation.TraceExporter]::ParseEventXml($prefix.Replace('SystemTime="2026-01-01T00:00:00Z"','SystemTime="bad"') + '</Event>') } 'Bad timestamp accepted.'
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('mailbox-export-fixtures-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporary) | Out-Null
try {
    $inputPath = Join-Path $temporary 'invalid.etl'
    $outputPath = Join-Path $temporary 'existing.jsonl'
    [IO.File]::WriteAllText($inputPath, 'synthetic invalid ETL')
    [IO.File]::WriteAllText($outputPath, 'preserve')
    Assert-Throws { [OpenBlade.MailboxObservation.TraceExporter]::Export($inputPath,$outputPath,10,1) } 'Existing output overwritten.'
    Assert-True ([IO.File]::ReadAllText($outputPath) -eq 'preserve') 'Existing output changed.'
    $newPath = Join-Path $temporary 'new.jsonl'
    $invalidEtlRejected = $false
    try { [OpenBlade.MailboxObservation.TraceExporter]::Export($inputPath,$newPath,4000000,1) } catch {
        if ($_.Exception.GetBaseException() -is [ArgumentOutOfRangeException]) { throw 'Reviewed event budget was rejected before inspecting the ETL.' }
        $invalidEtlRejected = $true
    }
    Assert-True $invalidEtlRejected 'Invalid ETL exported.'
    $overBudgetRejected = $false
    try { [OpenBlade.MailboxObservation.TraceExporter]::Export($inputPath,$newPath,4000001,1) } catch {
        $overBudgetRejected = $_.Exception.GetBaseException() -is [ArgumentOutOfRangeException]
    }
    Assert-True $overBudgetRejected 'Unbounded event request was accepted.'
    Assert-True (-not [IO.File]::Exists($newPath)) 'Invalid ETL published a report.'
    Assert-True (@([IO.Directory]::GetFiles($temporary,'.mailbox-export-*.tmp')).Count -eq 0) 'Owned partial temporary file leaked.'
} finally {
    # This exact UUID directory and its regular fixture files are owned by this test.
    foreach ($file in [IO.Directory]::GetFiles($temporary)) { [IO.File]::Delete($file) }
    [IO.Directory]::Delete($temporary, $false)
}
Write-Output "$script:checks offline exporter assertions passed; no trace started."
