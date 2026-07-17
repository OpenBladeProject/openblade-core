$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$capture = Join-Path $repositoryRoot 'src\OpenBlade.Capture\bin\Release\net10.0-windows\OpenBlade.Capture.exe'
$outputDirectory = 'C:\tmp\openblade-fixed-setter-validation-v6-long-ramp-20260717'
$stateFile = 'C:\tmp\openblade-fixed-setter-validation-v6-state.txt'
$restartService = $false
$captureExitCode = $null
$failure = $null

"elevated-started $([DateTimeOffset]::Now.ToString('O')) pid=$PID" |
    Set-Content -LiteralPath $stateFile -Encoding utf8

try {
    if (-not (Test-Path -LiteralPath $capture -PathType Leaf)) {
        throw "Capture executable was not found at '$capture'."
    }

    $service = Get-Service -Name 'OpenBlade'
    $restartService = $service.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Running
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

    if ($restartService) {
        "service-stop-started $([DateTimeOffset]::Now.ToString('O'))" |
            Set-Content -LiteralPath $stateFile -Encoding utf8
        Stop-Service -Name 'OpenBlade'
        (Get-Service -Name 'OpenBlade').WaitForStatus(
            [System.ServiceProcess.ServiceControllerStatus]::Stopped,
            [TimeSpan]::FromSeconds(15))
        "service-stopped $([DateTimeOffset]::Now.ToString('O'))" |
            Set-Content -LiteralPath $stateFile -Encoding utf8
    }

    "capture-started $([DateTimeOffset]::Now.ToString('O'))" |
        Set-Content -LiteralPath $stateFile -Encoding utf8
    $process = Start-Process -FilePath $capture `
        -ArgumentList @(
            'validate-performance-fan',
            'fixed5400',
            '--confirm-target',
            'RZ09-0581:1532:02E0:3.01') `
        -Wait `
        -PassThru `
        -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $outputDirectory 'fixed5400.json') `
        -RedirectStandardError (Join-Path $outputDirectory 'fixed5400.stderr.txt')
    $captureExitCode = $process.ExitCode
    "capture-exited $([DateTimeOffset]::Now.ToString('O')) exit=$captureExitCode" |
        Set-Content -LiteralPath $stateFile -Encoding utf8
    @{ fixed5400 = $captureExitCode } | ConvertTo-Json | Set-Content -LiteralPath (
        Join-Path $outputDirectory 'exit-codes.json') -Encoding utf8
}
catch {
    $failure = $_
}
finally {
    try {
        if ($restartService) {
            "service-restoration-started $([DateTimeOffset]::Now.ToString('O'))" |
                Set-Content -LiteralPath $stateFile -Encoding utf8
            Start-Service -Name 'OpenBlade'
            (Get-Service -Name 'OpenBlade').WaitForStatus(
                [System.ServiceProcess.ServiceControllerStatus]::Running,
                [TimeSpan]::FromSeconds(15))
        }
    }
    catch {
        if ($null -eq $failure) {
            $failure = $_
        }
    }

    $serviceStatus = (Get-Service -Name 'OpenBlade' -ErrorAction SilentlyContinue).Status
    if ($null -ne $failure) {
        "failed $([DateTimeOffset]::Now.ToString('O')) service=$serviceStatus $($failure.Exception.GetType().FullName): $($failure.Exception.Message)" |
            Set-Content -LiteralPath $stateFile -Encoding utf8
    }
    else {
        "finished $([DateTimeOffset]::Now.ToString('O')) exit=$captureExitCode service=$serviceStatus" |
            Set-Content -LiteralPath $stateFile -Encoding utf8
    }
}

if ($null -ne $failure) {
    throw $failure
}

exit $captureExitCode
