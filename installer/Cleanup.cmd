@echo off
:: Cleanup.cmd - uruchamia vigil-cleanup.ps1 z pomincieciem ExecutionPolicy.
:: Wymaga uruchomienia jako Administrator.

powershell.exe -NoLogo -ExecutionPolicy Bypass -File "%~dp0vigil-cleanup.ps1" %*

if %errorLevel% neq 0 (
    echo.
    echo [BLAD] Cleanup zakonczyl sie bledem. Kod wyjscia: %errorLevel%
)
pause
