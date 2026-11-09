@echo off
:: Build.cmd - runs build-package.ps1 regardless of the execution policy.
:: Parameters go to the script, e.g.:
::   Build.cmd -Configuration Debug
::   Build.cmd -SkipBuild

powershell.exe -NoLogo -ExecutionPolicy Bypass -File "%~dp0build-package.ps1" %*

if %errorLevel% neq 0 (
    echo.
    echo [ERROR] The build failed. Exit code: %errorLevel%
)
pause
