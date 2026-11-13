@echo off
title MES Demo Launcher
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0refresh-demo.ps1"
echo.
echo ============================================
echo  Tunnel keeps running in background.
echo  Closing this window will NOT stop it.
echo ============================================
pause
