# 启动 Cloudflare 快速隧道，把本机 5080 后端暴露到公网
# 用法：.\scripts\start-tunnel.ps1 [-Cloudflared <exe 路径>] [-Port 5080]
# 注意：每次启动生成的 https://xxx.trycloudflare.com 地址都不同，
#       换地址后要去 GitHub 仓库 Settings → Variables 更新 VITE_API_BASE，再重跑部署。
param(
    [string]$Cloudflared = "$env:USERPROFILE\tools\cloudflared.exe",
    [int]$Port = 5080
)

if (-not (Test-Path $Cloudflared)) {
    Write-Host "找不到 cloudflared：$Cloudflared" -ForegroundColor Red
    Write-Host "下载地址 https://github.com/cloudflare/cloudflared/releases/latest" -ForegroundColor Yellow
    exit 1
}

Write-Host "后端地址: http://localhost:$Port" -ForegroundColor Cyan
Write-Host "启动后在输出里找 https://xxxx.trycloudflare.com 这一行" -ForegroundColor Cyan
& $Cloudflared tunnel --url "http://localhost:$Port" --no-autoupdate
