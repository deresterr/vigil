@echo off
:: Cleanup.cmd - removes Vigil from this PC: vigil_setup.exe -Mode uninstall (D22).
:: Best effort: every step runs even when one fails, also on a half-removed installation
:: (e.g. C:\ProgramData\Vigil deleted from WinRE, see README). vigil_setup.exe asks for
:: administrator rights itself (UAC). Afterwards "vigil_setup.exe -Mode verify" checks that nothing is left.
::
:: Kept until the final cleanup; the menu of vigil_setup.exe (Uninstall) does the same.

"%~dp0vigil_setup.exe" -Mode uninstall
exit /b %errorLevel%
