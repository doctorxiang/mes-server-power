# 安装 / 移除「开机自动启动演示环境」
# 装：.\scripts\install-autostart.ps1
# 卸：.\scripts\install-autostart.ps1 -Remove
# 实现方式：在用户启动文件夹放一个 cmd，登录时静默执行 refresh-demo.ps1（不弹浏览器）
param([switch]$Remove)

$startup = [Environment]::GetFolderPath('Startup')
$entry = Join-Path $startup 'MES演示自启.cmd'
$script = Join-Path $PSScriptRoot 'refresh-demo.ps1'

if ($Remove) {
    if (Test-Path $entry) { Remove-Item $entry -Force; Write-Host "已移除开机自启：$entry" -ForegroundColor Green }
    else { Write-Host "本来就没装" -ForegroundColor DarkGray }
    return
}

if (-not (Test-Path $script)) { Write-Host "找不到 $script" -ForegroundColor Red; exit 1 }

$lines = @(
    '@echo off',
    'title MES Demo Launcher',
    "powershell -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$script`" -NoOpen"
)
[IO.File]::WriteAllText($entry, ($lines -join "`r`n") + "`r`n", (New-Object Text.UTF8Encoding($false)))

Write-Host "已启用开机自启：$entry" -ForegroundColor Green
Write-Host "登录后后台静默启动后端与隧道（不弹浏览器）" -ForegroundColor Cyan
Write-Host "取消：.\scripts\install-autostart.ps1 -Remove" -ForegroundColor DarkGray
