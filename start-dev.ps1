# 一键启动：MySQL(docker) 可选 + 后端 + 前端
# 用法：.\start-dev.ps1            # SQLite 快速演示（无需 MySQL）
#       .\start-dev.ps1 -WithMysql # 先起 docker MySQL 再启动（生产配置）

param(
    [switch]$WithMysql
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

Write-Host "== 服务器电源 MES 开发环境启动 ==" -ForegroundColor Cyan

if ($WithMysql) {
    Write-Host "[1/3] 启动 MySQL (docker compose)..." -ForegroundColor Yellow
    docker compose -f "$root\docker-compose.yml" up -d
    Start-Sleep -Seconds 8
    $env:ASPNETCORE_ENVIRONMENT = "Production"
} else {
    Write-Host "[1/3] SQLite 演示模式（跳过 MySQL）" -ForegroundColor DarkGray
    $env:ASPNETCORE_ENVIRONMENT = "Development"
}

Write-Host "[2/3] 启动后端 http://localhost:5080 ..." -ForegroundColor Yellow
Start-Process -FilePath "dotnet" -ArgumentList "run", "--no-launch-profile", "--urls", "http://localhost:5080" `
    -WorkingDirectory "$root\backend\MesApi" -WindowStyle Minimized

Write-Host "[3/3] 启动前端 http://localhost:5173 ..." -ForegroundColor Yellow
Start-Process -FilePath "cmd" -ArgumentList "/c", "npm run dev" `
    -WorkingDirectory "$root\frontend" -WindowStyle Minimized

Write-Host ""
Write-Host "启动完成：" -ForegroundColor Green
Write-Host "  前端   http://localhost:5173  （车间终端 / 看板入口）"
Write-Host "  后端   http://localhost:5080  （API + SignalR）"
Write-Host "  Adminer http://localhost:8081 （仅 -WithMysql，MySQL 管理界面）"
Write-Host ""
Write-Host "演示账号：admin / Admin@12345（管理员） operator1 / Operator@123（操作工）"
