@echo off
rem Double-click to remove the Civil 3D MCP plugin. Close Civil 3D first. Saved scripts and logs are kept.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Civil3DPlugin.ps1" -Uninstall
pause
