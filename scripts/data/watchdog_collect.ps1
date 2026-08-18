# Collect 看门狗 — 崩溃自动重启，7×24 守护
param([int]$HeartbeatSec = 30)

$proj = "$PSScriptRoot\..\..\src\TradingStudio"
$count = 0

while ($true) {
    $proc = Get-Process TradingStudio -ErrorAction SilentlyContinue
    if (-not $proc) {
        $count++
        $ts = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
        Write-Host "[$ts] #$count TradingStudio 已死，重启..."
        Start-Process dotnet -ArgumentList "run --no-build -- collect" -WorkingDirectory $proj -WindowStyle Minimized
    }
    Start-Sleep $HeartbeatSec
}
