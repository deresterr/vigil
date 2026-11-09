@echo off
:: Install.cmd - shortcut to vigil_setup.exe (D22) with the same parameters as before.
:: vigil_setup.exe asks for administrator rights itself (UAC).
::
::   Install.cmd                  wizard (nothing installed) or status and menu
::   Install.cmd -Mode verify     checks the installation, changes nothing
::   Install.cmd -Mode uninstall
::   Install.cmd -Mode update-config -PairingCode 12345678
::
:: Kept until the final cleanup; run vigil_setup.exe directly.

"%~dp0vigil_setup.exe" %*
exit /b %errorLevel%
