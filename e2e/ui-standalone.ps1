<#
.SYNOPSIS
    vigil_ui smoke test (CI, throwaway windows-latest runner): the lock screen in test mode, every lock reason,
    must close itself after --exit-after. A PR broke that (#121, the exit timer never ran) and only a VM showed it.
.PARAMETER PartsDir
    The folder with vigil_ui.exe (build-package.ps1's build\parts).
#>
param([Parameter(Mandatory)][string]$PartsDir)
$ErrorActionPreference = 'Stop'

$exe = Join-Path $PartsDir 'vigil_ui.exe'
$log = Join-Path $env:LOCALAPPDATA 'Vigil\ui.log'
$failed = 0

foreach ($mode in 'limit', 'night', 'manual', 'pause', 'server_unreachable', 'device_unpaired') {
    foreach ($lang in 'pl', 'en') {
        $p = Start-Process -FilePath $exe -PassThru -ArgumentList @(
            '--standalone-test', "--mode=$mode", "--lang=$lang", '--no-keyboard-blocker', '--exit-after=3')
        # CPU over seconds 1-3: an idle lock screen uses almost none; a busy UI thread starves its timers.
        Start-Sleep -Seconds 1
        $p.Refresh(); $cpu0 = $p.TotalProcessorTime.TotalMilliseconds
        Start-Sleep -Seconds 2
        $p.Refresh(); $cpu = if ($p.HasExited) { 'exited' } else { '{0:N0} ms' -f ($p.TotalProcessorTime.TotalMilliseconds - $cpu0) }
        if ($p.WaitForExit(20000)) {
            if ($p.ExitCode -eq 0) { "OK   $mode/$lang (CPU s1-3: $cpu)" }
            else { $failed++; "::error::$mode/${lang}: vigil_ui exited with $($p.ExitCode)" }
        } else {
            $failed++
            "::error::$mode/${lang}: vigil_ui still running 20 s after --exit-after=3 (CPU s1-3: $cpu)"
            Stop-Process -Id $p.Id -Force
        }
    }
}

'--- ui.log ---'
if (Test-Path $log) { Get-Content $log -Tail 80 } else { 'no ui.log' }
exit $failed
