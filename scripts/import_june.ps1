$ErrorActionPreference = "Continue"
$repoRoot = Resolve-Path "$PSScriptRoot\.."
$toolBoxDir = "$repoRoot\src\TradingStudio.ToolBox\bin\Debug\net10.0"
$dailyDir = "C:\Works\Datas\Jinshuyuan\Daily"
$historyDb = "$repoRoot\data\bars_history.duckdb"
$tempDir = "$repoRoot\data\temp_import"

New-Item -ItemType Directory -Force -Path $tempDir | Out-Null

$rarFiles = Get-ChildItem "$dailyDir\202606*.rar" | Sort-Object Name
$total = $rarFiles.Count
$success = 0
$fail = 0
$totalAppended = 0
$startAll = Get-Date

Write-Host "═══════════════════════════════════" -ForegroundColor Cyan
Write-Host "  Import June 2026 — $total daily RAR files" -ForegroundColor Cyan
Write-Host "  Target: $historyDb" -ForegroundColor Cyan
Write-Host "═══════════════════════════════════" -ForegroundColor Cyan

foreach ($rar in $rarFiles) {
    $date = $rar.BaseName  # "20260601"
    $dailyDb = "$tempDir\bars_$date.duckdb"

    Write-Host ""
    Write-Host "[$($success + $fail + 1)/$total] $date ($([math]::Round($rar.Length/1MB,0)) MB)" -ForegroundColor Yellow

    # Step 1: Import RAR → daily DB (use import-url with local RAR path)
    $startImport = Get-Date
    Push-Location $toolBoxDir
    try {
        # Build a local file URL
        $fileUrl = "file:///$($rar.FullName -replace '\\','/')"

        # Use import-jinshuyuan directly with --data-dir pointing to temp extract dir
        $extractDir = "$tempDir\extract_$date"
        New-Item -ItemType Directory -Force -Path $extractDir | Out-Null

        # Extract the RAR
        & "$repoRoot\tools\UnRAR.exe" x -y "$($rar.FullName)" "$extractDir\" 2>&1 | Out-Null

        # Import extracted CSVs
        $importResult = dotnet TradingStudio.ToolBox.dll import --input $extractDir --db $dailyDb 2>&1
        $importOk = ($LASTEXITCODE -eq 0)

        if ($importOk) {
            $importTime = [math]::Round(((Get-Date) - $startImport).TotalSeconds, 0)
            Write-Host "  Imported in ${importTime}s" -ForegroundColor Green

            # Step 2: Append to history
            $startAppend = Get-Date
            $appendResult = dotnet TradingStudio.ToolBox.dll append --source $dailyDb --target $historyDb 2>&1
            $appendOk = ($LASTEXITCODE -eq 0)
            $appendTime = [math]::Round(((Get-Date) - $startAppend).TotalSeconds, 0)

            if ($appendOk) {
                Write-Host "  Appended in ${appendTime}s" -ForegroundColor Green
                $success++
            } else {
                Write-Host "  Append FAILED" -ForegroundColor Red
                $fail++
            }
        } else {
            Write-Host "  Import FAILED" -ForegroundColor Red
            $fail++
        }
    } finally {
        Pop-Location
        # Cleanup extract dir
        Remove-Item -Recurse -Force $extractDir -ErrorAction SilentlyContinue
        Remove-Item -Force $dailyDb -ErrorAction SilentlyContinue
    }
}

$totalTime = [math]::Round(((Get-Date) - $startAll).TotalMinutes, 1)

Write-Host ""
Write-Host "═══════════════════════════════════" -ForegroundColor Green
Write-Host "  IMPORT COMPLETE" -ForegroundColor Green
Write-Host "═══════════════════════════════════" -ForegroundColor Green
Write-Host "  Success: $success / $total"
Write-Host "  Failed:  $fail"
Write-Host "  Time:    ${totalTime} min"
Write-Host "  Target:  $historyDb"
$size = (Get-Item $historyDb).Length / 1GB
Write-Host "  Size:    $([math]::Round($size, 2)) GB"
Write-Host "═══════════════════════════════════" -ForegroundColor Green

# Cleanup temp
Remove-Item -Recurse -Force $tempDir -ErrorAction SilentlyContinue
