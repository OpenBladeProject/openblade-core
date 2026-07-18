[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$UsbPcapDevice,

    [Parameter(Mandatory)]
    [ValidateRange(1, 127)]
    [int]$DeviceAddress,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$captureExecutable = Join-Path $repositoryRoot 'src\OpenBlade.Capture\bin\Release\net10.0-windows\OpenBlade.Capture.exe'
$startHelper = Join-Path $PSScriptRoot 'Start-UsbPcapCapture.ps1'
$stopHelper = Join-Path $PSScriptRoot 'Stop-UsbPcapCapture.ps1'
$usbPcapExecutable = 'C:\Program Files\USBPcap\USBPcapCMD.exe'
$pcapPath = Join-Path $OutputDirectory 'lighting-brightness-query.pcap'
$sessionPath = Join-Path $OutputDirectory 'lighting-brightness-query.usbpcap.json'
$queryOutputPath = Join-Path $OutputDirectory 'query.stdout.txt'
$queryErrorPath = Join-Path $OutputDirectory 'query.stderr.txt'
$statePath = Join-Path $OutputDirectory 'state.txt'
$capture = $null
$restartService = $false
$failure = $null

try {
    foreach ($requiredPath in @($captureExecutable, $startHelper, $stopHelper, $usbPcapExecutable)) {
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "Required capture component was not found: $requiredPath"
        }
    }

    if (Get-Process -Name 'USBPcapCMD' -ErrorAction SilentlyContinue) {
        throw 'Refusing to start while another USBPcapCMD process is active.'
    }

    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    foreach ($outputPath in @($pcapPath, $sessionPath, $queryOutputPath, $queryErrorPath)) {
        if (Test-Path -LiteralPath $outputPath) {
            throw "Refusing to overwrite existing capture output: $outputPath"
        }
    }

    "starting $([DateTimeOffset]::Now.ToString('O')) pid=$PID root=$UsbPcapDevice address=$DeviceAddress" |
        Set-Content -LiteralPath $statePath -Encoding utf8

    $service = Get-Service -Name 'OpenBlade' -ErrorAction Stop
    $restartService = $service.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Running
    if ($restartService) {
        Stop-Service -Name 'OpenBlade'
        (Get-Service -Name 'OpenBlade').WaitForStatus(
            [System.ServiceProcess.ServiceControllerStatus]::Stopped,
            [TimeSpan]::FromSeconds(15))
    }

    $capture = & $startHelper `
        -ExecutablePath $usbPcapExecutable `
        -ArgumentList @(
            '-d', $UsbPcapDevice,
            '--devices', $DeviceAddress.ToString([Globalization.CultureInfo]::InvariantCulture),
            '--inject-descriptors',
            '-s', '65535',
            '-o', $pcapPath) `
        -OutputPath $pcapPath `
        -SessionPath $sessionPath

    Start-Sleep -Milliseconds 750

    $query = Start-Process -FilePath $captureExecutable `
        -ArgumentList @('query-lighting-brightness') `
        -Wait `
        -PassThru `
        -WindowStyle Hidden `
        -RedirectStandardOutput $queryOutputPath `
        -RedirectStandardError $queryErrorPath
    if ($query.ExitCode -ne 0) {
        throw "The typed lighting brightness query failed with exit code $($query.ExitCode)."
    }

    "query-complete $([DateTimeOffset]::Now.ToString('O'))" |
        Set-Content -LiteralPath $statePath -Encoding utf8
}
catch {
    $failure = $_
}
finally {
    if ($null -ne $capture -and (Test-Path -LiteralPath $sessionPath -PathType Leaf)) {
        try {
            & $stopHelper -SessionPath $sessionPath | Out-Null
        }
        catch {
            if ($null -eq $failure) {
                $failure = $_
            }
        }
    }

    if ($restartService) {
        try {
            Start-Service -Name 'OpenBlade'
            (Get-Service -Name 'OpenBlade').WaitForStatus(
                [System.ServiceProcess.ServiceControllerStatus]::Running,
                [TimeSpan]::FromSeconds(15))
        }
        catch {
            if ($null -eq $failure) {
                $failure = $_
            }
        }
    }

    if ($null -ne $failure) {
        "failed $([DateTimeOffset]::Now.ToString('O')) $($failure.Exception.GetType().FullName): $($failure.Exception.Message)" |
            Set-Content -LiteralPath $statePath -Encoding utf8
    }
    else {
        "finished $([DateTimeOffset]::Now.ToString('O')) service=$((Get-Service -Name 'OpenBlade').Status)" |
            Set-Content -LiteralPath $statePath -Encoding utf8
    }
}

if ($null -ne $failure) {
    throw $failure
}
