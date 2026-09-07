@echo off
rem ============================================================
rem  SystemToolkit 一键构建主入口（V1-000）
rem  实现核心在 Build.ps1（三道产物防伪防线，见 04 分册 §1.3）
rem  用法：Tools\Build.bat [--no-test]
rem ============================================================
setlocal
chcp 65001 >nul
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build.ps1" %*
endlocal & exit /b %ERRORLEVEL%
