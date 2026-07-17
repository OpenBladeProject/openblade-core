@echo off
setlocal
set "STATE=C:\tmp\openblade-fixed-setter-validation-v6-state.txt"
set "LOCK=C:\tmp\openblade-fixed-setter-validation-v6.lock"
2>nul mkdir "%LOCK%"
if errorlevel 1 (
  echo A fixed-fan validation launcher is already active or needs inspection.
  echo Refusing to create another elevation request.
  pause
  exit /b 2
)
>"%STATE%" echo elevation-requested %DATE% %TIME%
echo OpenBlade fixed-fan long-ramp validation will request administrator approval.
echo It stops only OpenBlade, observes the 5400 RPM ramp for up to 120 seconds,
echo restores the saved mode, and restarts OpenBlade.
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "try { $p = Start-Process -FilePath 'powershell.exe' -Verb RunAs -PassThru -Wait -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File','%~dp0Invoke-FixedFanValidationV3.ps1'); exit $p.ExitCode } catch { ('launch-failed ' + [DateTimeOffset]::Now.ToString('O') + ' ' + $_.Exception.Message) | Set-Content -LiteralPath '%STATE%' -Encoding utf8; Write-Error $_; exit 1 }"
set "EXITCODE=%ERRORLEVEL%"
rmdir "%LOCK%"
echo.
type "%STATE%"
echo Launcher exit code: %EXITCODE%
echo You may close this window.
pause
exit /b %EXITCODE%
