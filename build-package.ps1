<#
.SYNOPSIS
    Buduje wszystkie komponenty PC (3 .NET projekty) i sklada paczke gotowa do vigil_install.ps1.

.DESCRIPTION
    Wykonuje 'dotnet publish' dla kazdego projektu (single-file, self-contained, win-x64),
    kopiuje wszystkie .exe + vigil_guardian.ps1 + vigil_install.ps1 do jednego katalogu output.

    Uruchom z root projektu (repo root):
        .\build-package.ps1

    Output: .\dist\ z 8 plikami:
        vigil_watchdog.exe     (~80 MB self-contained)
        vigil_lockscreen.exe   (~80 MB)
        vigil_tray.exe         (~30 MB, WinForms tray icon)
        vigil_guardian.ps1
        vigil_install.ps1
        Install.cmd            (launcher bez ExecutionPolicy)
        vigil-cleanup.ps1
        Cleanup.cmd            (launcher odinstalowania)

    Skopiuj caly .\dist\ na PC dziecka i uruchom Install.cmd.

.PARAMETER OutputDir
    Gdzie zlozyc paczke. Default: .\dist

.PARAMETER Configuration
    Release / Debug. Default: Release

.PARAMETER SkipBuild
    Pomin dotnet publish (przydatne gdy juz zbudowane, chcesz tylko skopiowac).
#>

[CmdletBinding()]
param(
    [string] $OutputDir = '',
    [ValidateSet('Release','Debug')] [string] $Configuration = 'Release',
    [switch] $SkipBuild
)

$ErrorActionPreference = 'Stop'

# $PSScriptRoot w domyslnej wartosci param() jest puste — obliczamy tu gdzie jest juz dostepne
if (-not $OutputDir) { $OutputDir = Join-Path $PSScriptRoot 'dist' }

$projects = @(
    @{ Dir = 'client\watchdog';   Exe = 'vigil_watchdog.exe' }
    @{ Dir = 'client\lockscreen'; Exe = 'vigil_lockscreen.exe' }
    @{ Dir = 'client\tray';       Exe = 'vigil_tray.exe' }
)

# === Validation ===
$missing = $projects | Where-Object { -not (Test-Path (Join-Path $PSScriptRoot $_.Dir)) }
if ($missing) {
    $names = ($missing | ForEach-Object { $_.Dir }) -join ', '
    throw "Brak katalogow projektow: $names. Uruchom z katalogu glownego repo."
}

# Sprawdz .NET SDK
try {
    $sdks = dotnet --list-sdks 2>&1
    $has8 = $sdks | Where-Object { $_ -match '^8\.' }
    if (-not $has8) {
        throw ".NET 8 SDK wymagany. Install: winget install Microsoft.DotNet.SDK.8"
    }
} catch [System.Management.Automation.CommandNotFoundException] {
    throw "dotnet CLI nie znaleziony. Install: winget install Microsoft.DotNet.SDK.8"
}

# === Output dir ===
if (Test-Path $OutputDir) {
    Write-Host "Czyszcze istniejacy $OutputDir" -ForegroundColor Yellow
    Remove-Item -Path $OutputDir -Recurse -Force
}
New-Item -Path $OutputDir -ItemType Directory -Force | Out-Null

# === Build ===
foreach ($p in $projects) {
    $projDir = Join-Path $PSScriptRoot $p.Dir
    # Wszystkie 3 projekty target net8.0-windows (vigil-watchdog tez, ze wzgledu na P/Invoke
    # do user32/advapi32 i SupportedOSPlatform attribute).
    # PS 5.1 Join-Path bierze tylko 2 argumenty, wiec budujemy sciezke krok po kroku.
    $publishDir = Join-Path $projDir "bin\$Configuration\net8.0-windows\win-x64\publish"

    if (-not $SkipBuild) {
        Write-Host ""
        Write-Host "==> Build $($p.Dir)" -ForegroundColor Cyan
        Push-Location $projDir
        try {
            dotnet publish -c $Configuration -r win-x64 `
                -p:PublishSingleFile=true `
                -p:SelfContained=true `
                -p:IncludeNativeLibrariesForSelfExtract=true `
                --nologo
            if ($LASTEXITCODE -ne 0) {
                throw "dotnet publish failed for $($p.Dir) (exit $LASTEXITCODE)"
            }
        } finally {
            Pop-Location
        }
    }

    # Skopiuj .exe do dist
    $srcExe = Join-Path $publishDir $p.Exe
    if (-not (Test-Path $srcExe)) {
        throw "Po build nie znajduje $srcExe - sprawdz output dotnet publish"
    }
    $dstExe = Join-Path $OutputDir $p.Exe
    Copy-Item -Path $srcExe -Destination $dstExe -Force
    $size = [Math]::Round((Get-Item $dstExe).Length / 1MB, 1)
    Write-Host "    OK $($p.Exe) ($size MB)" -ForegroundColor Green
}

# === Skopiuj non-build artifacts ===
$extras = @(
    @{ From = 'installer\vigil_guardian.ps1';   To = 'vigil_guardian.ps1' }
    @{ From = 'installer\vigil_install.ps1';    To = 'vigil_install.ps1' }
    @{ From = 'installer\Install.cmd';          To = 'Install.cmd' }
    @{ From = 'installer\vigil-cleanup.ps1';    To = 'vigil-cleanup.ps1' }
    @{ From = 'installer\Cleanup.cmd';          To = 'Cleanup.cmd' }
    @{ From = 'installer\vigil-cleanup-offline.cmd'; To = 'vigil-cleanup-offline.cmd' }
)
foreach ($e in $extras) {
    $src = Join-Path $PSScriptRoot $e.From
    $dst = Join-Path $OutputDir $e.To
    if (-not (Test-Path $src)) {
        throw "Brak $src"
    }
    Copy-Item -Path $src -Destination $dst -Force
    Write-Host "    OK $($e.To)" -ForegroundColor Green
}

# === Summary ===
Write-Host ""
Write-Host "==> Paczka gotowa: $OutputDir" -ForegroundColor Magenta
Get-ChildItem -Path $OutputDir | Format-Table @{N='Plik';E={$_.Name}}, @{N='Rozmiar';E={[Math]::Round($_.Length/1MB,1).ToString()+' MB'}}

Write-Host ""
Write-Host "Skopiuj caly katalog dist\ na PC dziecka i uruchom jako Administrator:" -ForegroundColor Yellow
Write-Host "  Tryb interaktywny (zalecany):  Install.cmd" -ForegroundColor Green
Write-Host "  Lub z parametrami:             vigil_install.ps1 -Mode install -ServerUrl '...' ..." -ForegroundColor Yellow