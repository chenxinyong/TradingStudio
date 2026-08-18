<#
.SYNOPSIS
    Parallel Jinshuyuan data import — one ToolBox process per year
.DESCRIPTION
    Each year runs as an independent process, writing to its own bars_YYYY.db.
    Progress monitored via bars_YYYY.progress.json files.
.PARAMETER Years - Year array (default 2021-2025)
.PARAMETER MaxParallel - Max concurrent processes (default 3)
.PARAMETER Layer - Import layer: main/active/all (default main)
.EXAMPLE
    .\import_jinshuyuan_parallel.ps1 -Years 2022,2023,2024,2025 -MaxParallel 3
#>

param(
    [int[]]$Years = @(2021, 2022, 2023, 2024, 2025),
    [string]$DataDir = "C:\Works\Datas\Jinshuyuan",
    [string]$OutputDir = "C:\Works\ClaudeCode\TradingStudio\data",
    [string]$Layer = "main",
    [int]$MaxParallel = 3,
    [switch]$Force,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

# ---- paths ----
$RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$ToolBoxExe = Join-Path $RepoRoot "src\TradingStudio.ToolBox\bin\Release\net10.0\TradingStudio.ToolBox.exe"
if (-not (Test-Path $ToolBoxExe)) {
    $ToolBoxExe = Join-Path $RepoRoot "src\TradingStudio.ToolBox\bin\Release\net10.0\win-x64\TradingStudio.ToolBox.exe"
}
if (-not (Test-Path $ToolBoxExe)) {
    Write-Error "ToolBox.exe not found. Build: dotnet build src/TradingStudio.ToolBox -c Release"
    exit 1
}
if (-not (Test-Path $DataDir)) {
    Write-Error "Data directory not found: $DataDir"
    exit 1
}

# ---- log dir ----
$ts = Get-Date -Format "yyyyMMdd_HHmmss"
$LogDir = Join-Path $OutputDir ("import_logs_" + $ts)
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Jinshuyuan Parallel Import" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ("  DataDir: " + $DataDir)
Write-Host ("  OutputDir: " + $OutputDir)
Write-Host ("  Layer: " + $Layer)
Write-Host ("  MaxParallel: " + $MaxParallel)
Write-Host ("  Logs: " + $LogDir)
Write-Host ""

# ---- build task list ----
$taskList = New-Object System.Collections.ArrayList

foreach ($y in $Years) {
    $dbPath = Join-Path $OutputDir ("bars_" + $y + ".db")
    $tmpDir = Join-Path $OutputDir ("_import_temp_" + $y)
    $from = [string]$y + "01"
    $to = [string]$y + "12"

    if ((Test-Path $dbPath) -and (-not $Force) -and (-not $DryRun)) {
        $sz = "{0:N1} GB" -f ((Get-Item $dbPath).Length / 1GB)
        Write-Host ("  SKIP " + $y + " — DB exists (" + $sz + "). Use -Force to overwrite.") -ForegroundColor Yellow
        continue
    }
    if ($Force -and (Test-Path $dbPath)) {
        Write-Host ("  OVERWRITE bars_" + $y + ".db") -ForegroundColor Magenta
        Remove-Item $dbPath -Force
    }

    $logFile = Join-Path $LogDir ("import_" + $y + ".log")
    $errFile = Join-Path $LogDir ("import_" + $y + ".err.log")

    # Build arguments: each value double-quoted to handle paths with spaces
    $argParts = @(
        "import-jinshuyuan",
        "--data-dir", ('"' + $DataDir + '"'),
        "--db", ('"' + $dbPath + '"'),
        "--layer", $Layer,
        "--from", $from,
        "--to", $to,
        "--temp-dir", ('"' + $tmpDir + '"')
    )
    if ($DryRun) {
        $argParts += "--dry-run"
    }
    $argStr = $argParts -join " "

    $null = $taskList.Add(@{
        Year = $y; DbPath = $dbPath; LogFile = $logFile; ErrFile = $errFile
        Args = $argStr; Proc = $null; Started = $null
    })

    Write-Host ("  QUEUE " + $y + ": " + $from + " -> " + $to) -ForegroundColor Gray
}

if ($taskList.Count -eq 0) {
    Write-Host ""
    Write-Host "All DBs already exist. Use -Force to re-import." -ForegroundColor Green
    exit 0
}

Write-Host ""
Write-Host ("Tasks queued: " + $taskList.Count) -ForegroundColor Green
Write-Host ""

# ---- main loop ----
$running = New-Object System.Collections.ArrayList
$done = New-Object System.Collections.ArrayList
$interval = 15

while ($taskList.Count -gt 0 -or $running.Count -gt 0) {

    # Launch new tasks if slots available
    while ($taskList.Count -gt 0 -and $running.Count -lt $MaxParallel) {
        $t = $taskList[0]
        $taskList.RemoveAt(0)

        Write-Host ("START " + $t.Year) -ForegroundColor Green
        $proc = Start-Process -FilePath $ToolBoxExe -ArgumentList $t.Args `
            -NoNewWindow -RedirectStandardOutput $t.LogFile -RedirectStandardError $t.ErrFile -PassThru
        $t.Proc = $proc
        $t.Started = Get-Date
        $null = $running.Add($t)
    }

    Start-Sleep -Seconds $interval

    # Check completed tasks
    $justDone = @()
    foreach ($rt in $running) {
        if ($rt.Proc.HasExited) {
            $justDone += $rt
        }
    }
    foreach ($t in $justDone) {
        $running.Remove($t)
        $null = $done.Add($t)
        $elapsed = "{0:hh\:mm\:ss}" -f ((Get-Date) - $t.Started)
        if ($t.Proc.ExitCode -eq 0) {
            Write-Host ("DONE  " + $t.Year + "  OK   " + $elapsed) -ForegroundColor Green
        } else {
            Write-Host ("DONE  " + $t.Year + "  FAIL " + $elapsed) -ForegroundColor Red
        }
    }

    # Show running progress
    foreach ($t in $running) {
        $pf = Join-Path $OutputDir ("bars_" + $t.Year + ".progress.json")
        $info = ""
        if (Test-Path $pf) {
            try {
                $pj = Get-Content $pf -Raw | ConvertFrom-Json
                $info = "  [" + $pj.progress + "] " + $pj.percent + "%  " + $pj.totalTicks + " ticks -> " + $pj.totalBars + " bars  ETA " + $pj.eta
            } catch { }
        }
        $elapsed = "{0:hh\:mm\:ss}" -f ((Get-Date) - $t.Started)
        Write-Host ("LIVE  " + $t.Year + "  " + $elapsed + $info) -ForegroundColor Cyan
    }
    Write-Host ("-" * 60)
}

# ---- summary ----
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  IMPORT COMPLETE" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

$totalBars = 0
$totalTicks = 0

foreach ($t in $done) {
    $pf = Join-Path $OutputDir ("bars_" + $t.Year + ".progress.json")
    $stats = ""
    if (Test-Path $pf) {
        try {
            $pj = Get-Content $pf -Raw | ConvertFrom-Json
            $totalBars += $pj.totalBars
            $totalTicks += $pj.totalTicks
            $stats = " | " + $pj.totalTicks + " ticks -> " + $pj.totalBars + " bars"
        } catch { }
    }
    $sz = ""
    if (Test-Path $t.DbPath) {
        $sz = "{0:N1} GB" -f ((Get-Item $t.DbPath).Length / 1GB)
    }
    $mark = if ($t.Proc.ExitCode -eq 0) { "OK" } else { "FAIL" }
    Write-Host (" " + $t.Year + ": " + $mark + "  DB: " + $sz + $stats) -ForegroundColor $(if ($t.Proc.ExitCode -eq 0) { "Green" } else { "Red" })
}

Write-Host ""
Write-Host ("Total: " + $totalTicks + " ticks -> " + $totalBars + " bars") -ForegroundColor Green
Write-Host ("Logs: " + $LogDir) -ForegroundColor Gray
Write-Host ""

$failed = ($done | Where-Object { $_.Proc.ExitCode -ne 0 }).Count
if ($failed -gt 0) {
    Write-Warning ($failed.ToString() + " import(s) failed. Check logs.")
    exit 1
}
exit 0
