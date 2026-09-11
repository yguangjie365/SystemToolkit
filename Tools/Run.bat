@echo off
rem ============================================================
rem  SystemToolkit one-click runner (with crash forensics)
rem  Usage: Tools\Run.bat
rem  Note : script waits until the app window is closed (normal).
rem  Output: Tools\logs\run-diagnostics.txt (auto-opens on abnormal exit)
rem  ASCII-ONLY on purpose: chcp 65001 + UTF-8 Chinese rem/echo lines made
rem  cmd's batch parser byte-shift and execute comment text as commands
rem  (garbled "NO_PROXY is not recognized" incident, 2026-09-11).
rem ============================================================
setlocal EnableExtensions
set "ROOT=%~dp0.."

echo [1/3] Closing old instance (if any) + incremental Release build...
tasklist /FI "IMAGENAME eq SystemToolkit.Shell.exe" 2>nul | find /I "SystemToolkit.Shell.exe" >nul
if not errorlevel 1 (
    echo     Old instance detected, killing...
    taskkill /F /IM SystemToolkit.Shell.exe >nul 2>&1
    timeout /t 1 /nobreak >nul
)
rem Bypass system proxy for NuGet in this process only (2026-09-11 verified):
rem a dead 127.0.0.1:7890 proxy causes NU1301; NO_PROXY suffix-match keeps restore online.
set "NO_PROXY=.nuget.org,.huaweicloud.com"
dotnet build "%ROOT%\SystemToolkit.sln" -c Release --nologo -v q
if errorlevel 1 (
    color 4F
    echo.
    echo *** BUILD FAILED, cannot launch ***
    pause
    exit /b 1
)

set "EXE=%ROOT%\src\SystemToolkit.Shell\bin\Release\net10.0-windows\SystemToolkit.Shell.exe"

if not exist "%ROOT%\Tools\logs" mkdir "%ROOT%\Tools\logs"
set "DIAG=%ROOT%\Tools\logs\run-diagnostics.txt"

echo [2/3] Launching SystemToolkit (--diag)...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
 "$p = Start-Process -FilePath '%EXE%' -ArgumentList '--diag' -PassThru;" ^
 "Start-Sleep -Seconds 3; $p.Refresh();" ^
 "if ($p.HasExited) { Write-Host ('!!! process exited within 3s, exit code ' + $p.ExitCode); exit 2 }" ^
 "for ($i = 0; $i -lt 30; $i++) { if ($p.MainWindowHandle -ne 0) { break }; Start-Sleep -Milliseconds 500; $p.Refresh() }" ^
 "if ($p.MainWindowHandle -ne 0) { Write-Host ('    window shown: ' + $p.MainWindowTitle + ' -- close it to continue diagnostics') }" ^
 "else { Write-Host '!!! no window within 15s (process alive, startup suspected broken)' }" ^
 "Wait-Process -Id $p.Id;" ^
 "exit $p.ExitCode"
set "EXITCODE=%ERRORLEVEL%"

if "%EXITCODE%"=="2" goto :diag

echo [3/3] Window closed, collecting diagnostics...

> "%DIAG%" echo ===== SystemToolkit run diagnostics =====
>> "%DIAG%" echo Time: %DATE% %TIME%
>> "%DIAG%" echo ExitCode: %EXITCODE%
>> "%DIAG%" echo.

>> "%DIAG%" echo ===== Crash log (Dispatcher / AppDomain / UnobservedTask) =====
if exist "%LOCALAPPDATA%\SystemToolkit\logs\crash-*.log" (
    type "%LOCALAPPDATA%\SystemToolkit\logs\crash-*.log"
) else (
    echo (no crash log)
)
>> "%DIAG%" echo.

>> "%DIAG%" echo ===== First-chance log (--diag, last 120 lines) =====
if exist "%LOCALAPPDATA%\SystemToolkit\logs\firstchance-*.log" (
    powershell -NoProfile -Command "Get-Content '%LOCALAPPDATA%\SystemToolkit\logs\firstchance-*.log' -ErrorAction SilentlyContinue | Select-Object -Last 120"
) else (
    echo (no first-chance log)
)

if "%EXITCODE%"=="0" (
    echo.
    echo App exited normally (code 0). Diagnostics: %DIAG%
) else (
    color 4F
    echo.
    echo *** App exited abnormally, code %EXITCODE% ***
    echo Diagnostics opened: %DIAG%
    start notepad "%DIAG%"
)

endlocal & exit /b %EXITCODE%

:diag
color 4F
echo.
echo *** Process exited at startup (before window). Diagnostics below ***
if exist "%LOCALAPPDATA%\SystemToolkit\logs\crash-*.log" type "%LOCALAPPDATA%\SystemToolkit\logs\crash-*.log"
echo Please send the output above to the developer.
pause
exit /b 2
