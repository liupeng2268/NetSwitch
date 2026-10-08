@echo off
chcp 65001 >nul 2>&1
title NetSwitch - IP Gateway Switcher

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0NetSwitch.ps1" %*

if errorlevel 1 (
    echo.
    echo Exited with error. Press any key to close...
    pause >nul
)
