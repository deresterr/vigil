@echo off
:: Build.cmd - uruchamia build-package.ps1 z pomincieciem ExecutionPolicy.
:: Parametry sa przekazywane do PS1, np.:
::   Build.cmd -Configuration Debug
::   Build.cmd -SkipBuild

powershell.exe -NoLogo -ExecutionPolicy Bypass -File "%~dp0build-package.ps1" %*

if %errorLevel% neq 0 (
    echo.
    echo [BLAD] Build zakonczyl sie bledem. Kod wyjscia: %errorLevel%
)
pause
