<#
.SYNOPSIS
  Writes artifacts/perf/perf-<date>-<sha>.md from Measure-Idle and Measure-Frames JSON.
.EXAMPLE
  .\New-PerfReport.ps1 -Idle idle.json [-Frames frames.json]
  .\New-PerfReport.ps1 -SelfTest
#>
param(
    [string]$Idle,
    [string]$Frames,
    [string]$ReportDir = (Join-Path $PSScriptRoot '..\..\artifacts\perf'),
    [switch]$SelfTest
)

function Get-Verdict($ok) { if ($ok) { 'pass' } else { 'FAIL' } }

function Build-Report($idle, $frames, $sha) {
    $s = $idle.summary
    $out = @(
        "# Perf $(Get-Date -Format yyyy-MM-dd) $sha", '',
        "Exe: ``$($idle.exe)``",
        "Machine: $($idle.machine), Windows $($idle.os); warm-up $($idle.warmupSeconds) s, then $($s.seconds) s over $($s.samples) samples.", '',
        '## Hidden idle', '',
        '| Metric | Measured | Target | Verdict |', '|---|---|---|---|',
        "| Working set, max | $($s.wsMaxMB) MB | under 80 MB | $(Get-Verdict ($s.wsMaxMB -lt 80)) |",
        "| CPU while hidden | $($s.cpuPercent) % ($($s.cpuSeconds) s) | about 0 % | $(Get-Verdict ($s.cpuPercent -lt 0.1)) |",
        "| Context switches | $($s.contextSwitches) over $($s.seconds) s | n/a | |",
        "| Wake-ups (context-switch proxy) | $($s.wakeupsPerSec) per s | n/a | |", '',
        "Private bytes max: $($s.privateMaxMB) MB. Mean working set: $($s.wsMeanMB) MB.", '',
        '## Morph frame times', ''
    )
    if ($frames) {
        $f = $frames
        $out += @('| Metric | Measured |', '|---|---|', "| Refresh | $($f.refreshHz) Hz (budget $($f.budgetMs) ms) |", "| Frames | $($f.frames) |",
            "| p50 / p95 / p99 / max | $($f.p50Ms) / $($f.p95Ms) / $($f.p99Ms) / $($f.maxMs) ms |",
            "| Dropped | $($f.droppedFrames) ($($f.droppedPercent) %) |", "| Achieved fps | $($f.achievedFps) |")
    } else { $out += 'Not measured: the shell has no composition or tick log yet.' }
    $out -join "`n"
}

if ($SelfTest) {
    $i = [pscustomobject]@{ exe = 'x.exe'; machine = 'm'; os = '10'; warmupSeconds = 1
        summary = [pscustomobject]@{ seconds = 10; samples = 3; wsMeanMB = 30; wsMaxMB = 90; privateMaxMB = 9; cpuSeconds = 0; cpuPercent = 0; contextSwitches = 5; wakeupsPerSec = 0.5 } }
    $md = Build-Report $i $null 'abc1234'
    if ($md -notmatch 'Working set, max \| 90 MB \| under 80 MB \| FAIL' -or $md -notmatch 'CPU while hidden \|[^|]*\| about 0 % \| pass' -or $md -notmatch 'Not measured') { throw "self-test failed:`n$md" }
    'New-PerfReport self-test ok'; return
}

if (-not $Idle) { throw 'pass -Idle <idle.json>' }
$sha = (git -C $PSScriptRoot rev-parse --short HEAD).Trim()
$f = if ($Frames) { Get-Content $Frames -Raw | ConvertFrom-Json }
New-Item -ItemType Directory -Force $ReportDir | Out-Null
$path = Join-Path $ReportDir "perf-$(Get-Date -Format yyyy-MM-dd)-$sha.md"
Build-Report (Get-Content $Idle -Raw | ConvertFrom-Json) $f $sha | Set-Content $path -Encoding utf8
Resolve-Path $path
