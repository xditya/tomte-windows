<#
.SYNOPSIS
  Frame-time probe. Reads per-frame timings and reports them against the monitor refresh interval.
.DESCRIPTION
  Input is one of:
    - the app's own tick log: one line per frame, first field a timestamp in milliseconds
    - a PresentMon CSV (has a MsBetweenPresents column), which measures real DWM presents
  The shell has no composition yet and writes no tick log, so there is no live input today;
  this is exercised with -SelfTest and takes the island's tick log once it exists.
.EXAMPLE
  .\Measure-Frames.ps1 -Log morph-ticks.txt -RefreshHz 144 -Out frames.json
  .\Measure-Frames.ps1 -SelfTest
#>
param(
    [string]$Log,
    [double]$RefreshHz = 0,
    [string]$Out = 'frames.json',
    [switch]$SelfTest
)

function Get-Percentile([double[]]$sorted, [double]$p) { $sorted[[math]::Min($sorted.Count - 1, [math]::Floor($p * $sorted.Count))] }

function Get-FrameSummary([double[]]$deltas, [double]$hz) {
    $budget = 1000 / $hz
    $s = $deltas | Sort-Object
    # a frame counts as dropped when it takes at least 1.5 refresh intervals
    $dropped = @($deltas | Where-Object { $_ -ge 1.5 * $budget }).Count
    [pscustomobject]@{
        refreshHz = $hz; budgetMs = [math]::Round($budget, 2); frames = $deltas.Count
        p50Ms = [math]::Round((Get-Percentile $s 0.5), 2); p95Ms = [math]::Round((Get-Percentile $s 0.95), 2)
        p99Ms = [math]::Round((Get-Percentile $s 0.99), 2); maxMs = [math]::Round($s[-1], 2)
        droppedFrames = $dropped; droppedPercent = [math]::Round(100 * $dropped / $deltas.Count, 2)
        achievedFps = [math]::Round(1000 / ($deltas | Measure-Object -Average).Average, 1)
    }
}

function Get-RefreshHz {
    Add-Type -ErrorAction SilentlyContinue -TypeDefinition @'
using System.Runtime.InteropServices;
public static class TomtePerfDisp {
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  public struct DEVMODE {
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
    public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra; public uint dmFields;
    public int dmPositionX, dmPositionY; public uint dmDisplayOrientation, dmDisplayFixedOutput;
    public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
    public ushort dmLogPixels; public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
    public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
  }
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool EnumDisplaySettings(string dev, int mode, ref DEVMODE dm);
  public static uint Hz() {
    var dm = new DEVMODE(); dm.dmSize = (ushort)Marshal.SizeOf(dm);
    EnumDisplaySettings(null, -1, ref dm);   // -1 = current mode of the primary display
    return dm.dmDisplayFrequency;
  }
}
'@
    [double][TomtePerfDisp]::Hz()
}

function Read-Deltas($path) {
    $lines = @(Get-Content $path | Where-Object { $_.Trim() })
    if ($lines[0] -match 'MsBetweenPresents') {
        $col = ($lines[0] -split ',').IndexOf('MsBetweenPresents')
        return [double[]]@($lines | Select-Object -Skip 1 | ForEach-Object { [double](($_ -split ',')[$col]) })
    }
    $t = [double[]]@($lines | ForEach-Object { [double](($_ -split '[,\s]+')[0]) })
    [double[]]@(1..($t.Count - 1) | ForEach-Object { $t[$_] - $t[$_ - 1] })
}

if ($SelfTest) {
    # 99 frames at 144 Hz and one 20 ms hitch: exactly one dropped frame
    $d = [double[]](@(1..99 | ForEach-Object { 6.94 }) + 20)
    $r = Get-FrameSummary $d 144
    if ($r.droppedFrames -ne 1 -or $r.maxMs -ne 20 -or $r.p50Ms -ne 6.94 -or $r.budgetMs -ne 6.94) { throw "self-test failed: $($r | ConvertTo-Json)" }
    $f = New-TemporaryFile
    "0`n10`n20`n40" | Set-Content $f
    if ((Read-Deltas $f).Count -ne 3) { throw 'tick-log parse failed' }
    Remove-Item $f
    if ((Get-RefreshHz) -lt 24) { throw 'refresh rate query failed' }
    'Measure-Frames self-test ok'; return
}

if (-not $Log) { throw 'pass -Log <tick log or PresentMon csv>' }
if ($RefreshHz -le 0) { $RefreshHz = Get-RefreshHz }
$r = Get-FrameSummary (Read-Deltas $Log) $RefreshHz
$r | ConvertTo-Json | Set-Content $Out -Encoding utf8
$r | Format-List
