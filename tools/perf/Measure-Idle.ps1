<#
.SYNOPSIS
  Starts an exe (which is expected to start hidden on its own) and records RAM, CPU, context switches and wake-ups for a fixed time.
.EXAMPLE
  .\Measure-Idle.ps1 -Exe ..\..\src\Tomte.Shell\bin\Release\net10.0-windows10.0.22621.0\Tomte.Shell.exe -Out idle.json
  .\Measure-Idle.ps1 -Exe ..\..\src\Tomte.Shell\bin\Release\net10.0-windows10.0.22621.0\win-x64\Tomte.Shell.exe -Arguments --rambler -Out idle.json
  .\Measure-Idle.ps1 -SelfTest
.NOTES
  Windows has no per-process wake-up counter outside ETW. Wake-ups here are thread context
  switches (each switch-in of an idle thread is a wake-up), from the thread performance data.
#>
param(
    [string]$Exe,
    [string[]]$Arguments = @(),
    [int]$Seconds = 300,
    [int]$IntervalSeconds = 5,
    [int]$WarmupSeconds = 10,
    [string]$Out = 'idle.json',
    # The window class that gets WM_CLOSE at the end; the process is killed only if that does not end it.
    [string]$CloseClass = 'TomteIsland',
    [switch]$SelfTest
)
$Out = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Out)

# Samples are cumulative ({t, cpu, cs, ws, pm}); totals come from first and last, memory from all.
function Get-IdleSummary($samples) {
    $a = $samples[0]; $b = $samples[-1]
    $span = $b.t - $a.t
    [pscustomobject]@{
        seconds         = [math]::Round($span, 1)
        samples         = $samples.Count
        wsMeanMB        = [math]::Round(($samples | Measure-Object ws -Average).Average / 1MB, 1)
        wsMaxMB         = [math]::Round(($samples | Measure-Object ws -Maximum).Maximum / 1MB, 1)
        privateMaxMB    = [math]::Round(($samples | Measure-Object pm -Maximum).Maximum / 1MB, 1)
        cpuSeconds      = [math]::Round($b.cpu - $a.cpu, 3)
        cpuPercent      = [math]::Round(100 * ($b.cpu - $a.cpu) / $span, 3)
        contextSwitches = $b.cs - $a.cs
        wakeupsPerSec   = [math]::Round(($b.cs - $a.cs) / $span, 2)
    }
}

# Raw per-thread counters vanish with the thread, so add up per-thread growth instead of summing raw values.
$seen = @{}; $total = 0L
function Add-ThreadGrowth($threads) {
    foreach ($th in $threads) {
        $prev = if ($seen.ContainsKey($th.IDThread)) { $seen[$th.IDThread] } else { 0 }
        $script:total += [math]::Max(0, [long]$th.ContextSwitchesPersec - $prev)
        $seen[$th.IDThread] = [long]$th.ContextSwitchesPersec
    }
}

if ($SelfTest) {
    Add-ThreadGrowth @([pscustomobject]@{ IDThread = 1; ContextSwitchesPersec = 10 }, [pscustomobject]@{ IDThread = 2; ContextSwitchesPersec = 5 })
    $before = $total
    Add-ThreadGrowth @([pscustomobject]@{ IDThread = 1; ContextSwitchesPersec = 14 })   # thread 2 exited
    if ($total - $before -ne 4) { throw "thread accumulation failed: grew by $($total - $before)" }
    $s = @(
        [pscustomobject]@{ t = 0;   cpu = 1.0; cs = 100; ws = 30MB; pm = 8MB },
        [pscustomobject]@{ t = 100; cpu = 1.5; cs = 300; ws = 34MB; pm = 9MB },
        [pscustomobject]@{ t = 200; cpu = 2.0; cs = 500; ws = 32MB; pm = 8MB }
    )
    $r = Get-IdleSummary $s
    if ($r.cpuPercent -ne 0.5 -or $r.contextSwitches -ne 400 -or $r.wakeupsPerSec -ne 2 -or $r.wsMaxMB -ne 34 -or $r.wsMeanMB -ne 32) { throw "self-test failed: $($r | ConvertTo-Json)" }
    'Measure-Idle self-test ok'; return
}

if (-not $Exe) { throw 'pass -Exe <path>' }
$Exe = (Resolve-Path $Exe).Path

# One ReadCategory is about 0.1 s; Get-CimInstance and Get-Counter take seconds on a busy machine.
$threadCategory = New-Object Diagnostics.PerformanceCounterCategory 'Thread'
function Read-Sample($p) {
    $p.Refresh()
    $d = $threadCategory.ReadCategory()
    $owner = $d['ID Process']; $tid = $d['ID Thread']; $cs = $d['Context Switches/sec']
    Add-ThreadGrowth @($owner.Keys | Where-Object { $owner[$_].RawValue -eq $p.Id } | ForEach-Object {
        [pscustomobject]@{ IDThread = $tid[$_].RawValue; ContextSwitchesPersec = $cs[$_].RawValue } })
    [pscustomobject]@{
        t = [Diagnostics.Stopwatch]::GetTimestamp() / [Diagnostics.Stopwatch]::Frequency
        cpu = $p.TotalProcessorTime.TotalSeconds; cs = $script:total; ws = $p.WorkingSet64; pm = $p.PrivateMemorySize64
    }
}

$proc = if ($Arguments) { Start-Process $Exe -ArgumentList $Arguments -PassThru } else { Start-Process $Exe -PassThru }
try {
    Start-Sleep $WarmupSeconds
    if ($proc.HasExited) { throw "process exited during warm-up (code $($proc.ExitCode))" }
    $started = Get-Date
    $samples = New-Object System.Collections.Generic.List[object]
    $end = $started.AddSeconds($Seconds)
    while ((Get-Date) -lt $end) {
        $samples.Add((Read-Sample $proc))
        Start-Sleep $IntervalSeconds
    }
    $samples.Add((Read-Sample $proc))
    $result = [pscustomobject]@{
        exe = $Exe; machine = $env:COMPUTERNAME; os = [Environment]::OSVersion.Version.ToString()
        started = $started.ToString('s'); warmupSeconds = $WarmupSeconds
        summary = Get-IdleSummary $samples.ToArray()
    }
    [IO.File]::WriteAllText($Out, ($result | ConvertTo-Json -Depth 4))
    $result.summary | Format-List
} finally {
    # WM_CLOSE to the island window first, so the shell quits as it would for the user: hotkeys released,
    # listeners removed, the tray icon taken down. Killing it is the fallback.
    if (-not $proc.HasExited) {
        Add-Type -Namespace MeasureIdle -Name Win -MemberDefinition @'
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
'@
        $h = [IntPtr]::Zero
        while (($h = [MeasureIdle.Win]::FindWindowEx([IntPtr]::Zero, $h, $CloseClass, $null)) -ne [IntPtr]::Zero) {
            $owner = 0; [void][MeasureIdle.Win]::GetWindowThreadProcessId($h, [ref]$owner)
            if ($owner -eq $proc.Id) { [void][MeasureIdle.Win]::PostMessage($h, 0x10, [IntPtr]::Zero, [IntPtr]::Zero) }
        }
        if ($proc.WaitForExit(5000)) { 'closed with WM_CLOSE' } else { Stop-Process $proc -Force; 'killed: WM_CLOSE did not end it' }
    }
}
