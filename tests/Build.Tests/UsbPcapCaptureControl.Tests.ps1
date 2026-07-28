[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$startHelper = Join-Path $repository 'captures\tools\Start-UsbPcapCapture.ps1'
$stopHelper = Join-Path $repository 'captures\tools\Stop-UsbPcapCapture.ps1'

function Assert-True {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Wait-ForFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [int]$TimeoutMilliseconds = 5000
    )

    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $Path -PathType Leaf) {
            return
        }

        Start-Sleep -Milliseconds 25
    }

    throw "Timed out waiting for $Path."
}

function ConvertTo-NativeArgument {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Value)

    if (($Value.Length -gt 0) -and ($Value -notmatch '[\s"]')) {
        return $Value
    }

    return '"' + ($Value -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"'
}

function Start-TestProcess {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$ArgumentList,
        [bool]$CreateNoWindow = $false,
        [bool]$RedirectOutput = $false
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $CreateNoWindow
    $startInfo.Arguments = (($ArgumentList | ForEach-Object { ConvertTo-NativeArgument $_ }) -join ' ')
    $startInfo.RedirectStandardOutput = $RedirectOutput
    $startInfo.RedirectStandardError = $RedirectOutput

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        $process.Dispose()
        throw "Could not start $FilePath."
    }

    return $process
}

function Invoke-StopHelper {
    param(
        [Parameter(Mandatory)][string]$SessionPath,
        [Parameter(Mandatory)][int]$TimeoutMilliseconds,
        [switch]$ForceFallback
    )

    $arguments = @(
        '-NoLogo',
        '-NoProfile',
        '-NonInteractive',
        '-ExecutionPolicy',
        'Bypass',
        '-File',
        $stopHelper,
        '-SessionPath',
        $SessionPath,
        '-TimeoutMilliseconds',
        $TimeoutMilliseconds.ToString([Globalization.CultureInfo]::InvariantCulture)
    )
    if ($ForceFallback) {
        $arguments += '-ForceFallback'
    }

    $process = Start-TestProcess -FilePath 'powershell.exe' -ArgumentList $arguments `
        -CreateNoWindow $true -RedirectOutput $true
    try {
        $standardOutput = $process.StandardOutput.ReadToEnd()
        $standardError = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            StandardOutput = $standardOutput
            StandardError = $standardError
        }
    }
    finally {
        $process.Dispose()
    }
}

function Test-ProcessAlive {
    param([Parameter(Mandatory)][Diagnostics.Process]$Process)

    try {
        return -not $Process.HasExited
    }
    catch {
        return $false
    }
}

function Assert-FinalizedPcap {
    param([Parameter(Mandatory)][string]$Path)

    $stream = [IO.File]::Open(
        $Path,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::ReadWrite)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        Assert-True (
            $reader.ReadUInt32() -eq [uint32]0xa1b2c3d4L) `
            'The finalized capture has an invalid PCAP magic number.'
        Assert-True (
            $reader.ReadUInt16() -eq 2 -and $reader.ReadUInt16() -eq 4) `
            'The finalized capture has an unsupported PCAP version.'
        [void]$reader.ReadInt32()
        [void]$reader.ReadUInt32()
        Assert-True ($reader.ReadUInt32() -eq 65535) `
            'The finalized capture has an unexpected snapshot length.'
        Assert-True ($reader.ReadUInt32() -eq 1) `
            'The finalized capture does not contain Ethernet frames.'

        [void]$reader.ReadUInt32()
        [void]$reader.ReadUInt32()
        $includedLength = $reader.ReadUInt32()
        $originalLength = $reader.ReadUInt32()
        Assert-True (
            $includedLength -eq 60 -and $originalLength -eq $includedLength) `
            'The finalized capture does not contain the complete synthetic packet.'

        $frame = $reader.ReadBytes([int]$includedLength)
        Assert-True (
            $frame.Length -eq $includedLength -and
            $frame[12] -eq 0x08 -and
            $frame[13] -eq 0x00 -and
            $stream.Position -eq $stream.Length) `
            'The finalized capture packet is truncated or malformed.'
    }
    finally {
        $reader.Dispose()
    }
}

Assert-True (Test-Path -LiteralPath $startHelper -PathType Leaf) 'The paired USBPcap start helper is missing.'
Assert-True (Test-Path -LiteralPath $stopHelper -PathType Leaf) 'The USBPcap stop helper is missing.'

$startSource = Get-Content -Raw -LiteralPath $startHelper
$stopSource = Get-Content -Raw -LiteralPath $stopHelper
$captureToolSource = (@(Get-ChildItem -LiteralPath (Split-Path -Parent $startHelper) -Filter '*.ps1' -File) |
    ForEach-Object { Get-Content -Raw -LiteralPath $_.FullName }) -join [Environment]::NewLine

# This gate deliberately runs before any process/control-event integration test.
# If a broadcast call is reintroduced, the test fails without risking its host.
Assert-True ($captureToolSource -notmatch '(?i)\bCTRL_C_EVENT\s*=|GenerateConsoleCtrlEvent\s*\(\s*CTRL_C_EVENT\b') `
    'Capture helpers must never use CTRL_C_EVENT.'
Assert-True ($captureToolSource -notmatch '(?i)GenerateConsoleCtrlEvent\s*\(\s*(?:0|0x0+)[uUlL]*\s*,') `
    'Capture helpers contain a CTRL_C_EVENT call.'
Assert-True ($captureToolSource -notmatch '(?i)GenerateConsoleCtrlEvent\s*\([^,\r\n]+,\s*(?:0|0x0+)[uUlL]*\s*\)') `
    'Capture helpers contain a process-group-zero broadcast.'
Assert-True ($captureToolSource -notmatch '(?i)GenerateConsoleCtrlEvent\s*\(\s*[^,\r\n]*Ctrl_?C[^,\r\n]*,') `
    'Capture helpers contain a named Ctrl+C event call.'
Assert-True ($startSource -match '(?i)\bCreate_?New_?Process_?Group\b') `
    'The start helper must create a dedicated process group.'
Assert-True ($startSource -match '(?i)0x0*200\b') `
    'The start helper must use the CREATE_NEW_PROCESS_GROUP value 0x200.'
Assert-True ($startSource -notmatch '(?i)CREATE_NEW_PROCESS_GROUP\s*\|\s*CREATE_NEW_CONSOLE|CREATE_NEW_CONSOLE\s*\|\s*CREATE_NEW_PROCESS_GROUP') `
    'CREATE_NEW_CONSOLE must not be combined with CREATE_NEW_PROCESS_GROUP because Windows ignores the group flag.'
Assert-True ($stopSource -match '(?i)\bCtrl_?Break_?Event\b') `
    'The stop helper must use CTRL_BREAK_EVENT.'
Assert-True ($stopSource -match '(?i)GenerateConsoleCtrlEvent\s*\(') `
    'The stop helper does not send a targeted console control event.'
Assert-True ($stopSource -match '(?i)GenerateConsoleCtrlEvent\s*\(\s*[^,\r\n]*(?:CTRL_BREAK_EVENT|CtrlBreakEvent|\b1[uUlL]*\b)[^,\r\n]*,\s*[^)\r\n]*ProcessGroupId[^)\r\n]*\)') `
    'The stop helper must target CTRL_BREAK_EVENT at the recorded process-group identifier.'
Assert-True ($stopSource -match '(?i)ProcessGroupId\s*(?:-eq\s*0|-le\s*0|<\s*1)') `
    'The stop helper must reject a zero process-group identifier.'
Assert-True ($stopSource -match '(?i)ProcessGroupId') `
    'The stop helper must consume the isolated process-group identifier.'
Assert-True ($stopSource -match '(?i)ForceFallback') `
    'The stop helper must require an explicit force-fallback option.'
Assert-True ($stopSource -match '(?i)buffered.{0,80}(?:lose|lost|loss)|(?:lose|lost|loss).{0,80}buffered') `
    'The explicit force fallback must warn that buffered capture data may be lost.'

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('OpenBlade.UsbPcapControl.Tests.' + [Guid]::NewGuid().ToString('N'))
$parentProcessId = $PID
$sentinel = $null
$capture = $null
$ignoredCapture = $null

try {
    New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
    $compiledProbe = Join-Path $testRoot 'ConsoleControlProbe.exe'
    $fakeUsbPcap = Join-Path $testRoot 'USBPcapCMD.exe'
    $sentinelExecutable = Join-Path $testRoot 'UnrelatedConsoleSentinel.exe'

    $probeSource = @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

internal static class ConsoleControlProbe
{
    private delegate bool ConsoleCtrlHandler(uint controlType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(ConsoleCtrlHandler handler, bool add);

    private static readonly ManualResetEvent ControlReceived = new ManualResetEvent(false);
    private static readonly ConsoleCtrlHandler Handler = HandleControl;
    private static int lastControlType = -1;

    private static bool HandleControl(uint controlType)
    {
        Interlocked.Exchange(ref lastControlType, unchecked((int)controlType));
        ControlReceived.Set();
        return true;
    }

    public static int Main(string[] args)
    {
        List<string> operationArguments = new List<string>();
        for (int index = 0; index < args.Length; index++)
        {
            if (String.Equals(args[index], "-o", StringComparison.OrdinalIgnoreCase))
            {
                if (++index >= args.Length)
                {
                    return 8;
                }
                continue;
            }
            operationArguments.Add(args[index]);
        }
        args = operationArguments.ToArray();

        if (args.Length == 0 || !SetConsoleCtrlHandler(Handler, true))
        {
            return 2;
        }

        try
        {
            switch (args[0])
            {
                case "capture":
                    return RunCapture(args);
                case "sentinel":
                    return RunSentinel(args);
                case "ignore":
                    return RunIgnoringCapture(args);
                default:
                    return 3;
            }
        }
        finally
        {
            SetConsoleCtrlHandler(Handler, false);
        }
    }

    private static int RunCapture(string[] args)
    {
        if (args.Length != 4)
        {
            return 4;
        }

        using (FileStream stream = new FileStream(args[1], FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        using (BinaryWriter writer = new BinaryWriter(stream))
        {
            WritePcapHeader(writer);
            stream.Flush(true);
            File.WriteAllText(args[2], "ready");

            if (!ControlReceived.WaitOne(TimeSpan.FromSeconds(20)))
            {
                return 5;
            }

            int controlType = Interlocked.CompareExchange(ref lastControlType, -1, -1);
            File.WriteAllText(args[3], controlType.ToString());
            WritePcapPacket(writer);
            stream.Flush(true);
        }

        return 0;
    }

    private static int RunSentinel(string[] args)
    {
        if (args.Length != 4)
        {
            return 6;
        }

        File.WriteAllText(args[1], "ready");
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (!File.Exists(args[2]) && DateTime.UtcNow < deadline)
        {
            if (ControlReceived.WaitOne(25))
            {
                int controlType = Interlocked.CompareExchange(ref lastControlType, -1, -1);
                File.WriteAllText(args[3], controlType.ToString());
                ControlReceived.Reset();
            }
        }

        return 0;
    }

    private static int RunIgnoringCapture(string[] args)
    {
        if (args.Length != 2)
        {
            return 7;
        }

        File.WriteAllText(args[1], "ready");
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            ControlReceived.WaitOne(25);
            ControlReceived.Reset();
        }

        return 0;
    }

    private static void WritePcapHeader(BinaryWriter writer)
    {
        writer.Write(0xa1b2c3d4u);
        writer.Write((ushort)2);
        writer.Write((ushort)4);
        writer.Write(0);
        writer.Write(0u);
        writer.Write(65535u);
        writer.Write(1u);
    }

    private static void WritePcapPacket(BinaryWriter writer)
    {
        byte[] frame = new byte[60]
        {
            0xff, 0xff, 0xff, 0xff, 0xff, 0xff,
            0x00, 0x11, 0x22, 0x33, 0x44, 0x55,
            0x08, 0x00,
            0x45, 0x00, 0x00, 0x20, 0x00, 0x00, 0x40, 0x00,
            0x40, 0x11, 0x00, 0x00, 0x7f, 0x00, 0x00, 0x01,
            0x7f, 0x00, 0x00, 0x01,
            0x04, 0xd2, 0x16, 0x2e, 0x00, 0x0c, 0x00, 0x00,
            0x74, 0x65, 0x73, 0x74,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
        };
        uint seconds = unchecked((uint)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds);
        writer.Write(seconds);
        writer.Write(0u);
        writer.Write((uint)frame.Length);
        writer.Write((uint)frame.Length);
        writer.Write(frame);
    }
}
'@

    Add-Type -TypeDefinition $probeSource -Language CSharp -OutputAssembly $compiledProbe `
        -OutputType ConsoleApplication
    Copy-Item -LiteralPath $compiledProbe -Destination $fakeUsbPcap
    Copy-Item -LiteralPath $compiledProbe -Destination $sentinelExecutable

    $sentinelReady = Join-Path $testRoot 'sentinel.ready'
    $sentinelStop = Join-Path $testRoot 'sentinel.stop'
    $sentinelEvent = Join-Path $testRoot 'sentinel.event'
    $sentinel = Start-TestProcess -FilePath $sentinelExecutable `
        -ArgumentList @('sentinel', $sentinelReady, $sentinelStop, $sentinelEvent)
    Wait-ForFile -Path $sentinelReady

    $pcapPath = Join-Path $testRoot 'graceful.pcap'
    $captureReady = Join-Path $testRoot 'capture.ready'
    $captureControl = Join-Path $testRoot 'capture.control'
    $sessionPath = Join-Path $testRoot 'graceful.session.json'

    & $startHelper -ExecutablePath $fakeUsbPcap `
        -ArgumentList @('capture', $pcapPath, $captureReady, $captureControl, '-o', $pcapPath) `
        -OutputPath $pcapPath -SessionPath $sessionPath | Out-Null
    Wait-ForFile -Path $sessionPath
    Wait-ForFile -Path $captureReady

    $sessionJson = Get-Content -Raw -LiteralPath $sessionPath
    $session = $sessionJson | ConvertFrom-Json
    Assert-True ($session.processId -gt 0) 'The start helper recorded an invalid capture PID.'
    Assert-True ($session.processGroupId -eq $session.processId) `
        'The capture process must be the leader of its isolated process group.'
    $capture = Get-Process -Id $session.processId -ErrorAction Stop

    $session.processGroupId = 0
    $session | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $sessionPath -Encoding UTF8
    $zeroGroupResult = Invoke-StopHelper -SessionPath $sessionPath -TimeoutMilliseconds 1000
    Assert-True ($zeroGroupResult.ExitCode -ne 0) 'The stop helper accepted process group zero.'
    Assert-True (Test-ProcessAlive $capture) 'The invalid process-group request changed the capture process.'
    Set-Content -LiteralPath $sessionPath -Value $sessionJson -Encoding UTF8 -NoNewline

    $stopResult = Invoke-StopHelper -SessionPath $sessionPath -TimeoutMilliseconds 5000
    Assert-True ($stopResult.ExitCode -eq 0) `
        "Graceful stop failed: $($stopResult.StandardOutput) $($stopResult.StandardError)"
    $capture.Refresh()
    Assert-True $capture.HasExited 'The fake USBPcap process did not exit after targeted CTRL_BREAK_EVENT.'
    Wait-ForFile -Path $captureControl
    Assert-True ((Get-Content -Raw -LiteralPath $captureControl) -eq '1') `
        'The capture received an event other than CTRL_BREAK_EVENT.'

    Assert-True ($PID -eq $parentProcessId) 'The PowerShell test host changed unexpectedly.'
    Assert-True ($null -ne (Get-Process -Id $parentProcessId -ErrorAction SilentlyContinue)) `
        'The parent PowerShell process did not survive capture shutdown.'
    Assert-True (Test-ProcessAlive $sentinel) 'An unrelated console process exited during capture shutdown.'
    Assert-True (-not (Test-Path -LiteralPath $sentinelEvent)) `
        'An unrelated console process received a console control event.'

    Assert-FinalizedPcap -Path $pcapPath

    $ignoredReady = Join-Path $testRoot 'ignored.ready'
    $ignoredSession = Join-Path $testRoot 'ignored.session.json'
    $ignoredOutput = Join-Path $testRoot 'ignored.pcap'
    & $startHelper -ExecutablePath $fakeUsbPcap -ArgumentList @('ignore', $ignoredReady, '-o', $ignoredOutput) `
        -OutputPath $ignoredOutput -SessionPath $ignoredSession | Out-Null
    Wait-ForFile -Path $ignoredSession
    Wait-ForFile -Path $ignoredReady
    $ignoredSessionData = Get-Content -Raw -LiteralPath $ignoredSession | ConvertFrom-Json
    $ignoredCapture = Get-Process -Id $ignoredSessionData.processId -ErrorAction Stop

    $noFallbackResult = Invoke-StopHelper -SessionPath $ignoredSession -TimeoutMilliseconds 1000
    Assert-True ($noFallbackResult.ExitCode -ne 0) `
        'A graceful-stop timeout unexpectedly force-stopped the capture.'
    Assert-True (Test-ProcessAlive $ignoredCapture) `
        'The capture was force-stopped without explicit fallback approval.'

    $forceResult = Invoke-StopHelper -SessionPath $ignoredSession -TimeoutMilliseconds 1000 -ForceFallback
    Assert-True ($forceResult.ExitCode -eq 0) `
        "The explicit force fallback failed: $($forceResult.StandardOutput) $($forceResult.StandardError)"
    $ignoredCapture.Refresh()
    Assert-True $ignoredCapture.HasExited 'The explicit force fallback did not stop the capture.'
    $forceMessage = $forceResult.StandardOutput + [Environment]::NewLine + $forceResult.StandardError
    Assert-True ($forceMessage -match '(?i)force') 'The stop helper did not surface use of force fallback.'
    Assert-True ($forceMessage -match '(?i)buffered.{0,80}(?:lose|lost|loss)|(?:lose|lost|loss).{0,80}buffered') `
        'The force fallback did not report possible buffered-capture loss.'

    Assert-True (Test-ProcessAlive $sentinel) `
        'An unrelated console process exited during force-fallback validation.'
    Assert-True (-not (Test-Path -LiteralPath $sentinelEvent)) `
        'An unrelated console process received a control event during fallback validation.'

    Write-Host 'USBPcap capture-control tests passed.'
}
finally {
    if ($null -ne $sentinel) {
        Set-Content -LiteralPath (Join-Path $testRoot 'sentinel.stop') -Value 'stop' -NoNewline -ErrorAction SilentlyContinue
        if (-not $sentinel.WaitForExit(2000)) {
            Stop-Process -Id $sentinel.Id -Force -ErrorAction SilentlyContinue
        }
        $sentinel.Dispose()
    }

    foreach ($candidate in @($capture, $ignoredCapture)) {
        if (($null -ne $candidate) -and (Test-ProcessAlive $candidate)) {
            Stop-Process -Id $candidate.Id -Force -ErrorAction SilentlyContinue
        }
        if ($null -ne $candidate) {
            $candidate.Dispose()
        }
    }

    if (Test-Path -LiteralPath $testRoot) {
        $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
        $resolvedTempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        if (-not $resolvedTestRoot.StartsWith($resolvedTempRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean unexpected test path $resolvedTestRoot."
        }
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
