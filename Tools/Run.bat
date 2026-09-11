@echo off
rem ============================================================
rem  SystemToolkit 一键运行（带崩溃取证）
rem  用法：Tools\Run.bat
rem  说明：脚本会等到窗口关闭才结束（这是正常现象）。
rem  产出：Tools\logs\run-diagnostics.txt（异常退出时自动弹出记事本）
rem ============================================================
setlocal EnableExtensions
chcp 65001 >nul
set "ROOT=%~dp0.."

echo [1/3] 关闭旧实例（如有）+ 构建增量（Release）...
tasklist /FI "IMAGENAME eq SystemToolkit.Shell.exe" 2>nul | find /I "SystemToolkit.Shell.exe" >nul
if not errorlevel 1 (
    echo     检测到旧实例仍在运行，正在关闭...
    taskkill /F /IM SystemToolkit.Shell.exe >nul 2>&1
    timeout /t 1 /nobreak >nul
)
rem NuGet 直连、绕过系统代理（2026-09-11 实测）：系统代理常指向 127.0.0.1:7890，
rem 代理软件没开时走它会直接 NU1301，脚本就卡在构建这一步。NO_PROXY 只作用于本进程，
rem 不改动系统代理设置（实测：无效代理 + NO_PROXY 后缀匹配时 NuGet 仍可正常联网）。
set "NO_PROXY=.nuget.org,.huaweicloud.com"
dotnet build "%ROOT%\SystemToolkit.sln" -c Release --nologo -v q
if errorlevel 1 (
    color 4F
    echo.
    echo *** 构建失败，无法启动 ***
    pause
    exit /b 1
)

set "EXE=%ROOT%\src\SystemToolkit.Shell\bin\Release\net10.0-windows\SystemToolkit.Shell.exe"

if not exist "%ROOT%\Tools\logs" mkdir "%ROOT%\Tools\logs"
set "DIAG=%ROOT%\Tools\logs\run-diagnostics.txt"

echo [2/3] 启动 SystemToolkit（--diag 诊断模式）...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
 "$p = Start-Process -FilePath '%EXE%' -ArgumentList '--diag' -PassThru;" ^
 "Start-Sleep -Seconds 3; $p.Refresh();" ^
 "if ($p.HasExited) { Write-Host ('!!! 进程 3 秒内已退出，退出码 ' + $p.ExitCode); exit 2 }" ^
 "for ($i = 0; $i -lt 10; $i++) { if ($p.MainWindowHandle -ne 0) { break }; Start-Sleep -Milliseconds 500; $p.Refresh() }" ^
 "if ($p.MainWindowHandle -ne 0) { Write-Host ('    窗口已弹出（标题: ' + $p.MainWindowTitle + '）—— 关闭窗口后本脚本继续收集诊断') }" ^
 "else { Write-Host '    !!! 15 秒内未见窗口（进程还在运行但无窗口，疑似启动异常）' }" ^
 "Wait-Process -Id $p.Id;" ^
 "exit $p.ExitCode"
set "EXITCODE=%ERRORLEVEL%"

if "%EXITCODE%"=="2" goto :diag

echo [3/3] 窗口已关闭，收集诊断信息...

> "%DIAG%" echo ===== SystemToolkit 运行诊断 =====
>> "%DIAG%" echo 时间: %DATE% %TIME%
>> "%DIAG%" echo 退出码: %EXITCODE%
>> "%DIAG%" echo.

>> "%DIAG%" echo ===== 崩溃日志（Dispatcher / AppDomain / UnobservedTask）=====
if exist "%LOCALAPPDATA%\SystemToolkit\logs\crash-*.log" (
    type "%LOCALAPPDATA%\SystemToolkit\logs\crash-*.log"
) else (
    echo （无崩溃日志）
)
>> "%DIAG%" echo.

>> "%DIAG%" echo ===== 首次异常记录（--diag 模式，最后 120 行）=====
if exist "%LOCALAPPDATA%\SystemToolkit\logs\firstchance-*.log" (
    powershell -NoProfile -Command "Get-Content '%LOCALAPPDATA%\SystemToolkit\logs\firstchance-*.log' -ErrorAction SilentlyContinue | Select-Object -Last 120"
) else (
    echo （无首次异常记录）
)

if "%EXITCODE%"=="0" (
    echo.
    echo 应用正常退出（退出码 0）。诊断文件：%DIAG%
) else (
    color 4F
    echo.
    echo *** 应用异常退出，退出码 %EXITCODE% ***
    echo 诊断文件已自动打开：%DIAG%
    start notepad "%DIAG%"
)

endlocal & exit /b %EXITCODE%

:diag
color 4F
echo.
echo *** 进程启动即退出（未到窗口阶段），诊断信息如下 ***
if exist "%LOCALAPPDATA%\SystemToolkit\logs\crash-*.log" type "%LOCALAPPDATA%\SystemToolkit\logs\crash-*.log"
echo 请将以上输出发给开发。
pause
exit /b 2
