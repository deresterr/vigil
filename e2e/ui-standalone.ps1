<#
.SYNOPSIS
    vigil_ui smoke test (CI, throwaway windows-latest runner): the lock screen in test mode, every lock reason,
    must close itself after --exit-after. On a VM the PR of #120 left it open (the log had the lock, no unlock).
.PARAMETER PartsDir
    The folder with vigil_ui.exe (build-package.ps1's build\parts).
#>
param([Parameter(Mandatory)][string]$PartsDir)
$ErrorActionPreference = 'Stop'

$exe = Join-Path $PartsDir 'vigil_ui.exe'
$log = Join-Path $env:LOCALAPPDATA 'Vigil\ui.log'
$failed = 0

# Every reason in both languages without the keyboard hook, then the first one with it, as deployed.
$runs = foreach ($mode in 'limit', 'night', 'manual', 'pause', 'server_unreachable', 'network_down', 'device_unpaired') {
    foreach ($lang in 'pl', 'en') { , @($mode, $lang, $false) }
}
$runs += , @('limit', 'pl', $true)

foreach ($run in $runs) {
    $mode, $lang, $hook = $run
    $name = if ($hook) { "$mode/$lang+hook" } else { "$mode/$lang" }
    $argv = @('--standalone-test', "--mode=$mode", "--lang=$lang", '--exit-after=3')
    if (-not $hook) { $argv += '--no-keyboard-blocker' }
    $p = Start-Process -FilePath $exe -PassThru -ArgumentList $argv
    # CPU over seconds 1-3: an idle lock screen uses little; a busy UI thread starves its timers.
    Start-Sleep -Seconds 1
    $p.Refresh(); $cpu0 = $p.TotalProcessorTime.TotalMilliseconds
    Start-Sleep -Seconds 2
    $p.Refresh()
    $cpu = if ($p.HasExited) { 'exited' } else { '{0:N0} ms' -f ($p.TotalProcessorTime.TotalMilliseconds - $cpu0) }
    if ($p.WaitForExit(20000)) {
        if ($p.ExitCode -eq 0) { "OK   $name (CPU s1-3: $cpu)" }
        else { $failed++; "::error::${name}: vigil_ui exited with $($p.ExitCode)" }
    } else {
        $failed++
        "::error::${name}: vigil_ui still running 20 s after --exit-after=3 (CPU s1-3: $cpu)"
        Stop-Process -Id $p.Id -Force
    }
}

'--- ui.log ---'
if (Test-Path $log) { Get-Content $log -Tail 80 } else { 'no ui.log' }
exit $failed
