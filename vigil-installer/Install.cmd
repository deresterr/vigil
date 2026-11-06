@echo off
:: Install.cmd - uruchamia vigil_install.ps1 z pomincieciem ExecutionPolicy.
:: Wymaga uruchomienia jako Administrator.
::
:: Tryb interaktywny (zalecany):
::   Install.cmd
::
:: Tryb z parametrami:
::   Install.cmd -Mode uninstall
::   Install.cmd -Mode reinstall -ServerUrl http://192.168.0.2:8088

powershell.exe -NoLogo -ExecutionPolicy Bypass -File "%~dp0vigil_install.ps1" %*

if %errorLevel% neq 0 (
    echo.
    echo [BLAD] Instalator zakonczyl sie bledem. Kod wyjscia: %errorLevel%
)
pause
