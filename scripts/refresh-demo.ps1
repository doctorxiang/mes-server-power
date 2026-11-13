# 一键刷新线上演示：启动后端 -> 起隧道 -> 更新仓库变量 -> 重新部署 -> 打开网站
# 用法：.\scripts\refresh-demo.ps1
#       .\scripts\refresh-demo.ps1 -SkipDeploy   # 只起隧道和更新变量，不触发部署
param(
    [string]$Repo = "doctorxiang/mes-server-power",
    [int]$Port = 5080,
    [string]$Cloudflared = "$env:USERPROFILE\tools\cloudflared.exe",
    [switch]$SkipDeploy
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

if (-not (Test-Path $Cloudflared)) {
    Write-Host "找不到 cloudflared：$Cloudflared" -ForegroundColor Red
    exit 1
}

# 1. 后端
Write-Host "[1/5] 检查后端 http://localhost:$Port ..." -ForegroundColor Cyan
$alive = $false
try { $null = Invoke-WebRequest "http://localhost:$Port/health/live" -UseBasicParsing -TimeoutSec 3; $alive = $true } catch { }
if (-not $alive) {
    Write-Host "      未运行，启动中（SQLite 演示模式）..." -ForegroundColor Yellow
    Start-Process -FilePath "dotnet" -ArgumentList "run", "--urls", "http://localhost:$Port" `
        -WorkingDirectory "$root\backend\MesApi" -WindowStyle Minimized
    for ($i = 0; $i -lt 45; $i++) {
        Start-Sleep -Seconds 2
        try { $null = Invoke-WebRequest "http://localhost:$Port/health/live" -UseBasicParsing -TimeoutSec 3; $alive = $true; break } catch { }
    }
}
if (-not $alive) { Write-Host "      后端启动失败，先单独跑 backend\MesApi 看日志" -ForegroundColor Red; exit 1 }
Write-Host "      后端就绪" -ForegroundColor Green

# 2. 隧道
Write-Host "[2/5] 启动 Cloudflare 隧道 ..." -ForegroundColor Cyan
$logOut = Join-Path $env:TEMP "cloudflared-demo.out.log"
$logErr = Join-Path $env:TEMP "cloudflared-demo.err.log"
Remove-Item $logOut, $logErr -Force -ErrorAction SilentlyContinue
Start-Process -FilePath $Cloudflared `
    -ArgumentList "tunnel", "--url", "http://localhost:$Port", "--no-autoupdate" `
    -RedirectStandardOutput $logOut -RedirectStandardError $logErr -WindowStyle Hidden

$publicUrl = $null
for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Seconds 1
    foreach ($f in @($logErr, $logOut)) {
        if (-not (Test-Path $f)) { continue }
        $m = Select-String -Path $f -Pattern 'https://[a-z0-9-]+\.trycloudflare\.com' -AllMatches -ErrorAction SilentlyContinue
        if ($m) { $publicUrl = $m[0].Matches[0].Value; break }
    }
    if ($publicUrl) { break }
}
if (-not $publicUrl) { Write-Host "      隧道地址没拿到，看日志：$logErr" -ForegroundColor Red; exit 1 }
Write-Host "      $publicUrl" -ForegroundColor Green

# 3. 连通性
Write-Host "[3/5] 验证外网可达 ..." -ForegroundColor Cyan
$ok = $false
for ($i = 0; $i -lt 10; $i++) {
    try {
        $r = Invoke-WebRequest "$publicUrl/health/ready" -UseBasicParsing -TimeoutSec 15
        if ($r.StatusCode -eq 200) { $ok = $true; break }
    } catch { Start-Sleep -Seconds 3 }
}
if (-not $ok) { Write-Host "      外网还不可达，等半分钟再说，或检查网络" -ForegroundColor Yellow }
else { Write-Host "      ready" -ForegroundColor Green }

# 4. 更新仓库变量
Write-Host "[4/5] 更新 $Repo 的 VITE_API_BASE ..." -ForegroundColor Cyan
$cred = "protocol=https`nhost=github.com`n`n" | git credential fill 2>$null
$token = ($cred | Select-String '^password=' | Select-Object -First 1).Line -replace '^password=', ''
if (-not $token) { Write-Host "      读不到 GitHub 凭据，先在仓库里 git push 一次登录" -ForegroundColor Red; exit 1 }

$headers = @{ Authorization = "token $token"; Accept = "application/vnd.github+json" }
$body = @{ name = "VITE_API_BASE"; value = $publicUrl } | ConvertTo-Json -Compress
$api = "https://api.github.com/repos/$Repo/actions/variables"
try {
    Invoke-RestMethod -Method Put -Uri "$api/VITE_API_BASE" -Headers $headers -Body $body -ContentType "application/json" | Out-Null
} catch {
    if ($_.Exception.Response.StatusCode.value__ -eq 404) {
        Invoke-RestMethod -Method Post -Uri $api -Headers $headers -Body $body -ContentType "application/json" | Out-Null
    } else { throw }
}
$cur = Invoke-RestMethod -Uri "$api/VITE_API_BASE" -Headers $headers
Write-Host "      当前值：$($cur.value)" -ForegroundColor Green

# 5. 重新部署
$owner, $name = $Repo.Split('/')
$site = "https://$owner.github.io/$name/"
if ($SkipDeploy) {
    Write-Host "[5/5] 跳过部署（-SkipDeploy）" -ForegroundColor DarkGray
} else {
    Write-Host "[5/5] 触发 Pages 重新部署 ..." -ForegroundColor Cyan
    $runs = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/actions/workflows/deploy-frontend.yml/runs?per_page=1" -Headers $headers
    $runId = $runs.workflow_runs[0].id
    if ($runId) {
        Invoke-RestMethod -Method Post -Uri "https://api.github.com/repos/$Repo/actions/runs/$runId/rerun" -Headers $headers | Out-Null
        Write-Host "      已触发 #$runId，约 1-2 分钟完成" -ForegroundColor Green
    } else {
        Write-Host "      没找到历史运行记录，去 Actions 页面手动 Run workflow" -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "隧道：$publicUrl" -ForegroundColor Cyan
Write-Host "网站：$site" -ForegroundColor Cyan
Write-Host "注意：隧道进程随本机运行，关机即断；下次直接再跑一次本脚本" -ForegroundColor DarkGray
Start-Process $site
