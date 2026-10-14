@echo off
rem Double-click to install the Civil 3D MCP plugin. Run with /uninstall to remove it.
if /i "%~1"=="/uninstall" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Civil3DPlugin.ps1" -Uninstall
) else (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Civil3DPlugin.ps1"
)
pause
