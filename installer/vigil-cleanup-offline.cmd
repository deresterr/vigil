@echo off
setlocal enabledelayedexpansion

REM ============================================================================
REM Vigil Cleanup — OFFLINE batch script for Windows Recovery (WinRE / ISO setup)
REM ----------------------------------------------------------------------------
REM Uzywa wylacznie komend dostepnych w czystym Recovery cmd: reg.exe, rmdir,
REM del, for. ZADNYCH zaleznosci od PowerShell, wmic, etc.
REM
REM Co kasuje (taki sam zakres co vigil-cleanup.ps1 -SystemDrive X: -Force):
REM   1. Service vigil_watchdog z SYSTEM hive (kazdy ControlSet)
REM   2. SafeBoot entries w Minimal i Network
REM   3. Pliki: ProgramData\Vigil (stara nazwa),
REM      tasks XML z Windows\System32\Tasks
REM   4. User policies w NTUSER.DAT KAZDEGO konta (Default + Users\*):
REM      DisableTaskMgr, DisableRegistryTools, DisableLockWorkstation,
REM      DisableChangePassword, NoLogoff, sticky/filter/toggle/mouse keys
REM      flags (przywrocone Windows defaulty), precision touchpad gestures.
REM
REM Uzycie z Recovery cmd:
REM   X:\Sources> vigil-cleanup-offline.cmd D:
REM
REM Gdzie D: to litera dysku z zainstalowanym Windows. W WinRE/setup ten dysk
REM zazwyczaj NIE jest C: (C: bywa zarezerwowany dla recovery image). Sprawdz:
REM   X:\Sources> dir C:\Windows         REM -> "File Not Found" -> nie ten
REM   X:\Sources> dir D:\Windows         REM -> wymienia katalogi -> to ten
REM ============================================================================

if "%~1"=="" goto :usage
if /I "%~1"=="/?" goto :usage
if /I "%~1"=="-h" goto :usage
if /I "%~1"=="--help" goto :usage

REM Normalizuj target drive: przyjmujemy "D" lub "D:" -> "D:"
set "TARGET=%~1"
set "TARGET=%TARGET::=%
set "TARGET=%TARGET%:"

REM Walidacja: czy ten dysk wyglada na Windows install?
if not exist "%TARGET%\Windows\System32\config\SOFTWARE" (
    echo [BLAD] %TARGET%\Windows\System32\config\SOFTWARE nie istnieje.
    echo        Czy %TARGET% to faktycznie dysk z zainstalowanym Windows?
    exit /b 2
)
if not exist "%TARGET%\Users\Default\NTUSER.DAT" (
    echo [BLAD] %TARGET%\Users\Default\NTUSER.DAT nie istnieje.
    echo        Sprawdz litere dysku przez 'dir %TARGET%\Windows'.
    exit /b 2
)

echo.
echo =============================================================
echo  Vigil Cleanup (offline / WinRE) - target: %TARGET%
echo =============================================================
echo.
echo Operacja usunie:
echo   - Service vigil_watchdog z SYSTEM hive
echo   - Pliki w %TARGET%\ProgramData\Vigil
echo   - Scheduled task XMLs (Vigil Guardian, Vigil Tray)
echo   - User policies (DisableTaskMgr itp.) w kazdym profilu user
echo.

if /I not "%~2"=="/Y" (
    set /p "ANSWER=Kontynuowac? (y/N): "
    if /I not "!ANSWER!"=="y" (
        echo Anulowano.
        exit /b 0
    )
)
echo.

REM ============================================================================
REM Krok 1: SYSTEM hive - kasuj service + SafeBoot entries
REM ============================================================================
echo ==^> Kasuje service vigil_watchdog z SYSTEM hive
reg unload HKLM\VigilCleanupSys >nul 2>&1
reg load HKLM\VigilCleanupSys "%TARGET%\Windows\System32\config\SYSTEM" >nul 2>&1
if errorlevel 1 (
    echo     [WARN] Nie udalo sie zaladowac SYSTEM hive - byc moze hive jest zablokowany
    echo            lub %TARGET% to zly dysk.
    goto :files
)

REM Service + SafeBoot w ControlSet001 i ControlSet002 (oba moga istniec)
for %%C in (ControlSet001 ControlSet002) do (
    reg query "HKLM\VigilCleanupSys\%%C" >nul 2>&1
    if not errorlevel 1 (
        reg delete "HKLM\VigilCleanupSys\%%C\Services\vigil_watchdog" /f >nul 2>&1
        if not errorlevel 1 echo     [OK] %%C\Services\vigil_watchdog skasowany

        reg delete "HKLM\VigilCleanupSys\%%C\Control\SafeBoot\Minimal\vigil_watchdog" /f >nul 2>&1
        if not errorlevel 1 echo     [OK] %%C\SafeBoot\Minimal\vigil_watchdog skasowany

        reg delete "HKLM\VigilCleanupSys\%%C\Control\SafeBoot\Network\vigil_watchdog" /f >nul 2>&1
        if not errorlevel 1 echo     [OK] %%C\SafeBoot\Network\vigil_watchdog skasowany

        REM Event Log sources (Application log) - source registrations

    )
)

reg unload HKLM\VigilCleanupSys >nul 2>&1


REM ============================================================================
REM Krok 2: Kasuj pliki Vigil
REM ============================================================================
:files
echo.
echo ==^> Kasuje pliki Vigil




REM Legacy: bypass.flag dir from installs before v0.5

for %%T in ("Vigil Guardian" "Vigil Tray") do (
    if exist "%TARGET%\Windows\System32\Tasks\%%~T" (
        del /f /q "%TARGET%\Windows\System32\Tasks\%%~T" 2>nul
        if not exist "%TARGET%\Windows\System32\Tasks\%%~T" (
            echo     [OK] Task XML "%%~T" usuniety
        ) else (
            echo     [BLAD] Task XML "%%~T" - nie udalo sie
        )
    )
)


REM ============================================================================
REM Krok 3: User policies - iteruje po Default + kazdym profilu user
REM ============================================================================
echo.
echo ==^> Czyszcze user policies w NTUSER.DAT kazdego konta

REM Najpierw Default profile (wplywa na nowo tworzone konta)
call :clean_user_hive "%TARGET%\Users\Default\NTUSER.DAT" "Default"

REM Potem wszystkie inne profile - filtrujemy te ktore maja NTUSER.DAT
for /D %%U in ("%TARGET%\Users\*") do (
    set "PROF_NAME=%%~nxU"
    REM Skip Default (juz zrobiony), Public, All Users, Default User
    if /I not "!PROF_NAME!"=="Default" (
        if /I not "!PROF_NAME!"=="Public" (
            if /I not "!PROF_NAME!"=="All Users" (
                if /I not "!PROF_NAME!"=="Default User" (
                    if exist "%%~U\NTUSER.DAT" (
                        call :clean_user_hive "%%~U\NTUSER.DAT" "!PROF_NAME!"
                    )
                )
            )
        )
    )
)

echo.
echo =============================================================
echo  Cleanup zakonczony
echo =============================================================
echo.
echo Po reboocie systemu (target: %TARGET%):
echo   - Wszystkie blokady i policy beda zdjete dla wszystkich kont user
echo   - Service vigil_watchdog nie wstanie (skasowany z rejestru)
echo   - Pliki w ProgramData skasowane
echo.
echo Jesli installer wyzaczyl WinRE przez -DisableWinRE, w online Windows wykonaj:
echo   reagentc /enable
echo.
exit /b 0


REM ============================================================================
REM Subrutyna: :clean_user_hive ^<NTUSER.DAT path^> ^<label^>
REM Mountuje NTUSER.DAT, kasuje policies + restore accessibility, unmount.
REM ============================================================================
:clean_user_hive
set "HIVE=%~1"
set "LABEL=%~2"

if not exist "%HIVE%" (
    echo     [skip] %LABEL%: %HIVE% nie istnieje
    goto :eof
)

REM Defensive: upewnij sie ze poprzedni unload zaszedl
reg unload HKLM\VigilCleanupUser >nul 2>&1

reg load HKLM\VigilCleanupUser "%HIVE%" >nul 2>&1
if errorlevel 1 (
    echo     [WARN] %LABEL%: nie udalo sie zamontowac NTUSER.DAT ^(zalogowany?^)
    goto :eof
)

REM --- Kasuj policy values ---
reg delete "HKLM\VigilCleanupUser\Software\Microsoft\Windows\CurrentVersion\Policies\System" /v DisableTaskMgr        /f >nul 2>&1
reg delete "HKLM\VigilCleanupUser\Software\Microsoft\Windows\CurrentVersion\Policies\System" /v DisableRegistryTools  /f >nul 2>&1
reg delete "HKLM\VigilCleanupUser\Software\Microsoft\Windows\CurrentVersion\Policies\System" /v DisableLockWorkstation /f >nul 2>&1
reg delete "HKLM\VigilCleanupUser\Software\Microsoft\Windows\CurrentVersion\Policies\System" /v DisableChangePassword  /f >nul 2>&1
reg delete "HKLM\VigilCleanupUser\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer" /v NoLogoff /f >nul 2>&1

REM --- Restore accessibility shortcuts (Windows defaulty) ---
reg add "HKLM\VigilCleanupUser\Control Panel\Accessibility\StickyKeys"        /v Flags /t REG_SZ /d 510 /f >nul 2>&1
reg add "HKLM\VigilCleanupUser\Control Panel\Accessibility\Keyboard Response" /v Flags /t REG_SZ /d 126 /f >nul 2>&1
reg add "HKLM\VigilCleanupUser\Control Panel\Accessibility\ToggleKeys"        /v Flags /t REG_SZ /d 62  /f >nul 2>&1
reg add "HKLM\VigilCleanupUser\Control Panel\Accessibility\MouseKeys"         /v Flags /t REG_SZ /d 62  /f >nul 2>&1

REM --- Restore precision touchpad gestures ---
reg add "HKLM\VigilCleanupUser\Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad" /v ThreeFingerSlideEnabled /t REG_DWORD /d 1 /f >nul 2>&1
reg add "HKLM\VigilCleanupUser\Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad" /v FourFingerSlideEnabled  /t REG_DWORD /d 1 /f >nul 2>&1
reg add "HKLM\VigilCleanupUser\Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad" /v ThreeFingerTapEnabled   /t REG_DWORD /d 1 /f >nul 2>&1
reg add "HKLM\VigilCleanupUser\Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad" /v FourFingerTapEnabled    /t REG_DWORD /d 1 /f >nul 2>&1

REM Unmount - reg.exe samo dba o flush przed unload
reg unload HKLM\VigilCleanupUser >nul 2>&1
if errorlevel 1 (
    echo     [WARN] %LABEL%: unload nie powiodl sie - hive moze pozostac zablokowany do reboota
) else (
    echo     [OK] %LABEL% - policies cofniete
)

goto :eof


REM ============================================================================
:usage
echo.
echo Uzycie:  vigil-cleanup-offline.cmd ^<litera_dysku^> [/Y]
echo.
echo   ^<litera_dysku^>  dysk z zainstalowanym Windows ^(np. D:^)
echo   /Y              pomin pytanie potwierdzenia
echo.
echo Przyklad:
echo   vigil-cleanup-offline.cmd D:
echo   vigil-cleanup-offline.cmd D: /Y
echo.
echo Skrypt dziala wylacznie OFFLINE (Windows na docelowym dysku NIE moze byc
echo aktywny). Uruchom z Windows Recovery Environment / Command Prompt z ISO.
echo.
echo Aby znalezc dysk z Windows:
echo   diskpart -^> list volume       (pokaze litery i labele)
echo   dir D:\Windows                ^(test czy to ten dysk^)
echo.
exit /b 1
