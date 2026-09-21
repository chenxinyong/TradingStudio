# TradingStudio Windows 服务注册 (开机自启 + 崩溃自愈)
# 用法 (管理员 PowerShell):
#   .\install-service.ps1            安装并启动
#   .\install-service.ps1 uninstall  卸载
#   .\install-service.ps1 restart    重启
param([ValidateSet("install","uninstall","restart")]$Action = "install")

$svc = "TradingStudio"
$exe = Join-Path $PSScriptRoot "TradingStudio.exe"

# 服务进程的环境变量: cloud → 加载 appsettings.cloud.json
[Environment]::SetEnvironmentVariable("DOTNET_ENVIRONMENT", "cloud", "Machine")

switch ($Action) {
    "install" {
        if (-not (Test-Path $exe)) { Write-Host "[ERROR] 找不到 $exe" -ForegroundColor Red; exit 1 }

        if (Get-Service $svc -ErrorAction SilentlyContinue) {
            Write-Host "[info] 服务已存在，直接启动"
        } else {
            New-Service -Name $svc `
                -BinaryPathName "`"$exe`" live" `
                -DisplayName "TradingStudio 实盘引擎" `
                -Description "量化交易引擎 (live 模式, HTTP :5001)" `
                -StartupType Automatic
            # 崩溃自愈: 失败后 5s/15s/60s 递增重启, 24h 后重置计数
            & sc.exe failure $svc reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
            Write-Host "[ok] 服务已注册 (自动启动 + 崩溃自愈)"
        }

        Start-Service $svc
        Start-Sleep 3
        Get-Service $svc | Format-Table -AutoSize Status, Name, DisplayName
    }
    "uninstall" {
        Stop-Service $svc -ErrorAction SilentlyContinue
        & sc.exe delete $svc
        Write-Host "[ok] 服务已卸载"
    }
    "restart" {
        Restart-Service $svc
        Get-Service $svc | Format-Table -AutoSize Status, Name, DisplayName
    }
}
