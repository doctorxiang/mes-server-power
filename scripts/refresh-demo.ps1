# 一键刷新线上演示环境（可重复执行，不会重复开隧道）
#   1) 后端没起就起
#   2) 隧道活着就复用，否则新建
#   3) 仓库变量与当前地址不一致才更新
#   4) 变量变了才重新部署
#   5) 打开网站（-NoOpen 可关闭）
# 用法：.\scripts\refresh-demo.ps1 [-NoOpen] [-SkipDeploy] [-Force]
param(
    [string]$Repo = "doctorxiang/mes-server-power",
    [int]$Port = 5080,
    [string]$Cloudflared = "$env:USERPROFILE\tools\cloudflared.exe",
    [switch]$NoOpen,
    [switch]$SkipDeploy,
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

# 环境里可能同时存在 HTTP_PROXY 与 http_proxy（大小写重复），Start-Process 复制环境时会报字典冲突
for ($i = 0; $i -lt 4; $i++) {
    $keys = [Environment]::GetEnvironmentVariables('Process').Keys | Where-Object { $_ -match '(?i)_proxy$' }
    if (-not $keys) { break }
    foreach ($k in $keys) { [Environment]::SetEnvironmentVariable($k, $null, 'Process') }
}

$root = Split-Path $PSScriptRoot -Parent
$stateFile = Join-Path $env:TEMP "mes-demo-tunnel.json"
$owner, $name = $Repo.Split('/')
$site = "https://$owner.github.io/$name/"

function Test-Url([string]$u) {
    try { return ((Invoke-WebRequest $u -UseBasicParsing -TimeoutSec 10).StatusCode -eq 200) } catch { return $false }
}

# git 不一定在 PATH 里（比如从桌面双击启动时），按常见位置兜底
function Get-GitExe {
    $c = Get-Command git -ErrorAction SilentlyContinue
    if ($c) { return $c.Source }
    foreach ($p in @("$env:ProgramFiles\Git\cmd\git.exe", "${env:ProgramFiles(x86)}\Git\cmd\git.exe", "$env:LOCALAPPDATA\Programs\Git\cmd\git.exe")) {
        if (Test-Path $p) { return $p }
    }
    $pg = Get-ChildItem "$env:USERPROFILE\.workbuddy\binaries\PortableGit\versions\*\bin\git.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($pg) { return $pg.FullName }
    return $null
}

if (-not (Test-Path $Cloudflared)) {
    Write-Host "找不到 cloudflared：$Cloudflared" -ForegroundColor Red
    exit 1
}

# ---------- 1. 后端 ----------
Write-Host "[1/5] 后端 http://localhost:$Port ..." -ForegroundColor Cyan -NoNewline
$alive = Test-Url "http://localhost:$Port/health/live"
if (-not $alive) {
    Write-Host " 未运行，启动中" -ForegroundColor Yellow
    $dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
    if (-not $dotnet) { $dotnet = "$env:ProgramFiles\dotnet\dotnet.exe" }
    Start-Process -FilePath $dotnet -ArgumentList "run", "--urls", "http://localhost:$Port" `
        -WorkingDirectory "$root\backend\MesApi" -WindowStyle Minimized
    for ($i = 0; $i -lt 45; $i++) {
        Start-Sleep -Seconds 2
        if (Test-Url "http://localhost:$Port/health/live") { $alive = $true; break }
    }
}
if (-not $alive) { Write-Host " 启动失败，手动跑 backend\MesApi 看日志" -ForegroundColor Red; exit 1 }
Write-Host " 就绪" -ForegroundColor Green

# ---------- 2. 隧道 ----------
Write-Host "[2/5] 隧道 ..." -ForegroundColor Cyan -NoNewline
$publicUrl = $null
if (-not $Force -and (Test-Path $stateFile)) {
    try {
        $st = Get-Content $stateFile -Raw | ConvertFrom-Json
        if ($st.pid -and (Get-Process -Id $st.pid -ErrorAction SilentlyContinue) -and (Test-Url "$($st.url)/health/ready")) {
            $publicUrl = $st.url
        }
    } catch { }
}
if ($publicUrl) {
    Write-Host " 复用 $publicUrl" -ForegroundColor Green
} else {
    Write-Host " 新建中" -ForegroundColor Yellow
    # 旧进程可能还活着但域名已被 Cloudflare 注销，先清掉避免堆积
    Get-Process cloudflared -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
    $logOut = Join-Path $env:TEMP "cloudflared-demo.out.log"
    $logErr = Join-Path $env:TEMP "cloudflared-demo.err.log"
    Remove-Item $logOut, $logErr -Force -ErrorAction SilentlyContinue
    $proc = Start-Process -FilePath $Cloudflared `
        -ArgumentList "tunnel", "--url", "http://localhost:$Port", "--no-autoupdate" `
        -RedirectStandardOutput $logOut -RedirectStandardError $logErr -WindowStyle Hidden -PassThru
    for ($i = 0; $i -lt 45; $i++) {
        Start-Sleep -Seconds 1
        foreach ($f in @($logErr, $logOut)) {
            if (-not (Test-Path $f)) { continue }
            $m = Select-String -Path $f -Pattern 'https://[a-z0-9-]+\.trycloudflare\.com' -AllMatches -ErrorAction SilentlyContinue
            if ($m) { $publicUrl = $m[0].Matches[0].Value; break }
        }
        if ($publicUrl) { break }
    }
    if (-not $publicUrl) { Write-Host " 没拿到地址，看 $logErr" -ForegroundColor Red; exit 1 }
    @{ pid = $proc.Id; url = $publicUrl } | ConvertTo-Json | Set-Content $stateFile -Encoding UTF8
    Write-Host " $publicUrl" -ForegroundColor Green
    if (-not (Test-Url "$publicUrl/health/ready")) {
        for ($i = 0; $i -lt 10; $i++) { Start-Sleep -Seconds 3; if (Test-Url "$publicUrl/health/ready") { break } }
    }
}

# ---------- 3. 仓库变量 ----------
Write-Host "[3/5] 仓库变量 VITE_API_BASE ..." -ForegroundColor Cyan -NoNewline
$git = Get-GitExe
$cred = $null
if ($git) {
    $env:GCM_INTERACTIVE = 'never'
    $cred = "protocol=https`nhost=github.com`n`n" | & $git credential fill 2>$null
}
$token = ($cred | Select-String '^password=' | Select-Object -First 1).Line -replace '^password=', ''
$headers = @{}
$changed = $false
if (-not $token) {
    Write-Host " 读不到 GitHub 凭据，跳过（先在仓库里 git push 一次）" -ForegroundColor Yellow
} else {
    $headers = @{ Authorization = "token $token"; Accept = "application/vnd.github+json" }
    $varUrl = "https://api.github.com/repos/$Repo/actions/variables/VITE_API_BASE"
    $changed = $true
    try {
        $cur = Invoke-RestMethod -Uri $varUrl -Headers $headers -TimeoutSec 20
        if ($cur.value -eq $publicUrl) { $changed = $false }
    } catch { }
    if ($changed) {
        $body = @{ name = "VITE_API_BASE"; value = $publicUrl } | ConvertTo-Json -Compress
        try {
            Invoke-RestMethod -Method Patch -Uri $varUrl -Headers $headers -Body $body -ContentType "application/json" -TimeoutSec 25 | Out-Null
        } catch {
            if ($_.Exception.Response.StatusCode.value__ -eq 404) {
                Invoke-RestMethod -Method Post -Uri "https://api.github.com/repos/$Repo/actions/variables" -Headers $headers -Body $body -ContentType "application/json" -TimeoutSec 25 | Out-Null
            } else { throw }
        }
        Write-Host " 已更新" -ForegroundColor Green
    } else {
        Write-Host " 无变化" -ForegroundColor DarkGray
    }
}

# ---------- 4. 部署 ----------
if ($SkipDeploy) {
    Write-Host "[4/5] 跳过部署（-SkipDeploy）" -ForegroundColor DarkGray
} elseif ($changed) {
    Write-Host "[4/5] 触发 Pages 重新部署 ..." -ForegroundColor Cyan -NoNewline
    $runs = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/actions/workflows/deploy-frontend.yml/runs?per_page=1" -Headers $headers -TimeoutSec 20
    if ($runs.workflow_runs.Count -gt 0) {
        Invoke-RestMethod -Method Post -Uri "https://api.github.com/repos/$Repo/actions/runs/$($runs.workflow_runs[0].id)/rerun" -Headers $headers -TimeoutSec 25 | Out-Null
        Write-Host " 已触发，1-2 分钟后生效" -ForegroundColor Green
    } else {
        Write-Host " 无历史运行，去 Actions 手动 Run workflow" -ForegroundColor Yellow
    }
} else {
    Write-Host "[4/5] 地址未变，无需重新部署" -ForegroundColor DarkGray
}

# ---------- 5. 打开 ----------
Write-Host "[5/5] 隧道 $publicUrl" -ForegroundColor Green
Write-Host "      网站 $site" -ForegroundColor Green
if (-not $NoOpen) { Start-Process $site }
