<#
.SYNOPSIS
    Builds the client and puts the package together: one vigil_setup.exe (#76).

.DESCRIPTION
    'dotnet publish' of the three projects (single-file, self-contained, win-x64) into the parts folder,
    then the package: vigil_setup.exe with vigil_watchdog.exe, vigil_ui.exe and vigil_ui's native WPF
    libraries appended after its .NET bundle, gzipped, with a trailer (format: client\setup\Core\Package.cs,
    the only reader).

    Nothing is extracted at run time (D24): the projects publish native libraries next to their exe, and
    only vigil_ui may have any, exactly VigilLayout.UiNativeLibraries (the build fails otherwise).

    Run from the repo root:
        .\build-package.ps1

    Output:
        dist\vigil_setup.exe          the package: double click = wizard / menu, -Mode ... for scripts
        build\parts\                  the files on their own: the e2e compares the installed ones with them

.PARAMETER OutputDir
    Where the package goes. Default: .\dist

.PARAMETER PartsDir
    Where the separate binaries go. Default: .\build\parts

.PARAMETER Configuration
    Release / Debug. Default: Release

.PARAMETER Version
    Version of every binary (-p:Version): a release takes the tag (e.g. 0.8.0), CI 0.0.0-ci.<run>.
    Default: 0.0.0-dev (as client\Directory.Build.props). vigil_setup compares the installed version with its own.

.PARAMETER SkipBuild
    Skip dotnet publish (already built: only put the package together).
#>

[CmdletBinding()]
param(
    [string] $OutputDir = '',
    [string] $PartsDir = '',
    [ValidateSet('Release','Debug')] [string] $Configuration = 'Release',
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')] [string] $Version = '0.0.0-dev',
    [switch] $SkipBuild
)

$ErrorActionPreference = 'Stop'

# $PSScriptRoot is empty inside param() defaults (Windows PowerShell 5.1)
if (-not $OutputDir) { $OutputDir = Join-Path $PSScriptRoot 'dist' }
if (-not $PartsDir) { $PartsDir = Join-Path (Join-Path $PSScriptRoot 'build') 'parts' }

# Natives: the libraries a project publishes next to its exe; the same list as VigilLayout.UiNativeLibraries.
$projects = @(
    @{ Dir = 'client\watchdog';   Exe = 'vigil_watchdog.exe'; Natives = @() }
    @{ Dir = 'client\ui';         Exe = 'vigil_ui.exe'
       Natives = @('D3DCompiler_47_cor3.dll', 'PenImc_cor3.dll', 'PresentationNative_cor3.dll',
                   'vcruntime140_cor3.dll', 'wpfgfx_cor3.dll') }
    @{ Dir = 'client\setup';      Exe = 'vigil_setup.exe'; Natives = @() }
)
# Appended to vigil_setup.exe, in this order.
$payload = @('vigil_watchdog.exe', 'vigil_ui.exe') + $projects[1].Natives

# === Validation ===
$missing = $projects | Where-Object { -not (Test-Path (Join-Path $PSScriptRoot $_.Dir)) }
if ($missing) {
    $names = ($missing | ForEach-Object { $_.Dir }) -join ', '
    throw "Project folders missing: $names. Run from the repo root."
}

try {
    $sdks = dotnet --list-sdks 2>&1
    $has10 = $sdks | Where-Object { $_ -match '^10\.' }
    if (-not $has10) {
        throw ".NET 10 SDK required. Install: winget install Microsoft.DotNet.SDK.10"
    }
} catch [System.Management.Automation.CommandNotFoundException] {
    throw "dotnet CLI not found. Install: winget install Microsoft.DotNet.SDK.10"
}

foreach ($dir in $OutputDir, $PartsDir) {
    if (Test-Path $dir) { Remove-Item -Path $dir -Recurse -Force }
    New-Item -Path $dir -ItemType Directory -Force | Out-Null
}

# === Build ===
foreach ($p in $projects) {
    $projDir = Join-Path $PSScriptRoot $p.Dir
    # Windows PowerShell 5.1 Join-Path takes two arguments only.
    $publishDir = Join-Path $projDir "bin\$Configuration\net10.0-windows\win-x64\publish"

    if (-not $SkipBuild) {
        Write-Host ""
        Write-Host "==> Build $($p.Dir)" -ForegroundColor Cyan
        Push-Location $projDir
        try {
            # One quoted string for -p:Version: splatted, 0.0.0-ci.89 reached dotnet mangled (MSB1001).
            dotnet publish -c $Configuration -r win-x64 `
                -p:PublishSingleFile=true `
                -p:SelfContained=true `
                "-p:Version=$Version" `
                --nologo
            if ($LASTEXITCODE -ne 0) {
                throw "dotnet publish failed for $($p.Dir) (exit $LASTEXITCODE)"
            }
        } finally {
            Pop-Location
        }
    }

    $srcExe = Join-Path $publishDir $p.Exe
    if (-not (Test-Path $srcExe)) {
        throw "$srcExe not found after the build - check the dotnet publish output"
    }
    # Everything published besides the exe (symbols aside) must be a listed native library: anything else
    # would be missing on the PC, or would have to be extracted at run time.
    $extra = @(Get-ChildItem -Path $publishDir -File |
        Where-Object { $_.Name -ne $p.Exe -and $_.Extension -ne '.pdb' } | ForEach-Object { $_.Name } | Sort-Object)
    $expected = @($p.Natives | Sort-Object)
    if (($extra -join ',') -ne ($expected -join ',')) {
        throw "$($p.Dir) published [$($extra -join ', ')] next to $($p.Exe), expected [$($expected -join ', ')]: update this list and VigilLayout.UiNativeLibraries"
    }
    foreach ($name in @($p.Exe) + $p.Natives) {
        Copy-Item -Path (Join-Path $publishDir $name) -Destination (Join-Path $PartsDir $name) -Force
    }
}

# === Package ===
# [vigil_setup.exe][entry]...[trailer], little-endian (BinaryWriter):
#   entry:   int32 name length, name (UTF-8), int64 size, int64 stored length, SHA-256 (32 B), gzip
#   trailer: "VIGILPK1", int64 offset of the first entry, int32 entries
function Write-Package([string] $Setup, [string[]] $Files, [string] $Target) {
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    $out = [System.IO.File]::Create($Target)
    try {
        $w = New-Object System.IO.BinaryWriter($out)
        $in = [System.IO.File]::OpenRead($Setup)
        try { $in.CopyTo($out) } finally { $in.Dispose() }
        $offset = $out.Position

        foreach ($file in $Files) {
            $name = $utf8.GetBytes([System.IO.Path]::GetFileName($file))
            $sha = [System.Security.Cryptography.SHA256]::Create()
            $in = [System.IO.File]::OpenRead($file)
            try { $hash = $sha.ComputeHash($in); $size = $in.Length } finally { $in.Dispose(); $sha.Dispose() }

            # Gzipped into memory first: the entry header carries the stored length.
            $stored = New-Object System.IO.MemoryStream
            $gzip = New-Object System.IO.Compression.GZipStream($stored, [System.IO.Compression.CompressionMode]::Compress, $true)
            $in = [System.IO.File]::OpenRead($file)
            try { $in.CopyTo($gzip) } finally { $in.Dispose(); $gzip.Dispose() }

            $w.Write([int32] $name.Length)
            $w.Write($name)
            $w.Write([int64] $size)
            $w.Write([int64] $stored.Length)
            $w.Write($hash)
            $w.Flush()
            $stored.Position = 0
            $stored.CopyTo($out)
            $stored.Dispose()
        }

        $w.Write([System.Text.Encoding]::ASCII.GetBytes('VIGILPK1'))
        $w.Write([int64] $offset)
        $w.Write([int32] $Files.Count)
        $w.Flush()
    } finally {
        $out.Dispose()
    }
}

Write-Host ""
Write-Host "==> Package" -ForegroundColor Cyan
$package = Join-Path $OutputDir 'vigil_setup.exe'
Write-Package -Setup (Join-Path $PartsDir 'vigil_setup.exe') `
    -Files ($payload | ForEach-Object { Join-Path $PartsDir $_ }) -Target $package

# === Summary ===
$mb = { param($f) [Math]::Round((Get-Item $f).Length / 1MB, 1).ToString() + ' MB' }
Get-ChildItem -Path $PartsDir | ForEach-Object { Write-Host ("    {0,-20} {1}" -f $_.Name, (& $mb $_.FullName)) }
Write-Host ""
Write-Host "==> Package ready: $package ($(& $mb $package))" -ForegroundColor Magenta
Write-Host "Copy it to the child's PC and run it:" -ForegroundColor Yellow
Write-Host "  Wizard / menu:    vigil_setup.exe  (UAC from the manifest)" -ForegroundColor Green
Write-Host "  With parameters:  vigil_setup.exe -Mode install -ServerUrl '...' -PairingCode '...'" -ForegroundColor Yellow
