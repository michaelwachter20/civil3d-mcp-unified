@echo off
rem Double-click to install (or update) the Civil 3D MCP plugin for the current Windows user.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Civil3DPlugin.ps1"
pause
