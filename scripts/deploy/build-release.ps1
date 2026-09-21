# 组装云端 live 部署包
# 用法: powershell -ExecutionPolicy Bypass -File scripts\deploy\build-release.ps1 [-SkipBuild]
param([switch]$SkipBuild)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$proj = Join-Path $root "src\TradingStudio"
$publish = Join-Path $proj "bin\Release\publish"
$version = "0.5.0"
$outDir = Join-Path $root "release\live\TradingStudio-$version"
$zip = Join-Path $root "release\live\TradingStudio-live-$version-win-x64.zip"

Write-Host "== 1/4 发布 ==" -ForegroundColor Cyan
if (-not $SkipBuild) {
    # 必须用 -o 指定输出目录: SelfContained 条件依赖 PublishDir, 否则是框架依赖发布
    dotnet publish (Join-Path $proj "TradingStudio.csproj") -c Release -o (Join-Path $proj "bin\Release\publish")
    if ($LASTEXITCODE -ne 0) { Write-Host "[ERROR] 发布失败" -ForegroundColor Red; exit 1 }
}

# 自包含自检 (云端无 .NET Runtime)
if (-not (Test-Path (Join-Path $publish "coreclr.dll"))) {
    Write-Host "[ERROR] 非自包含发布 (缺少 coreclr.dll) — 检查 SelfContained 条件" -ForegroundColor Red; exit 1
}

# 关键文件自检
$required = @("TradingStudio.exe", "appsettings.json", "appsettings.cloud.json",
              "symbols.json", "start-live.ps1", "install-service.ps1", "README-部署.md",
              "ftdc2c_ctp.dll", "duckdb.dll")
foreach ($f in $required) {
    if (-not (Test-Path (Join-Path $publish $f))) {
        Write-Host "[ERROR] 发布产物缺少 $f" -ForegroundColor Red; exit 1
    }
}
if (-not (Test-Path (Join-Path $publish "strategies\live"))) { Write-Host "[ERROR] 缺少 strategies\live" -ForegroundColor Red; exit 1 }

Write-Host "== 2/4 清理开发残留 ==" -ForegroundColor Cyan
foreach ($p in @("health.json", "crash.log", "appsettings.local.json", "bars_live.db")) {
    Remove-Item (Join-Path $publish $p) -Force -ErrorAction SilentlyContinue
}
Remove-Item (Join-Path $publish "logs") -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $publish "data") -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "== 3/4 注入预热库 ==" -ForegroundColor Cyan
$warmup = Join-Path $proj "data\bars_warmup.duckdb"
if (-not (Test-Path $warmup)) {
    Write-Host "[WARN] 预热库不存在，先跑: python scripts\deploy\gen_warmup.py" -ForegroundColor Yellow
} else {
    New-Item -ItemType Directory -Force (Join-Path $publish "data") | Out-Null
    Copy-Item $warmup (Join-Path $publish "data\bars_warmup.duckdb") -Force
    Write-Host "      已注入 bars_warmup.duckdb ($([math]::Round((Get-Item $warmup).Length/1MB, 2)) MB)"
}

Write-Host "== 4/4 组装部署包 ==" -ForegroundColor Cyan
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
New-Item -ItemType Directory -Force $outDir | Out-Null
Copy-Item (Join-Path $publish "*") $outDir -Recurse -Force
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $outDir "*") -DestinationPath $zip
Write-Host ""
Write-Host "[ok] 部署包就绪:" -ForegroundColor Green
Write-Host "     目录: $outDir"
Write-Host "     zip : $zip ($([math]::Round((Get-Item $zip).Length/1MB, 1)) MB)"
