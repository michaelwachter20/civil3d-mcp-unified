# Installs or uninstalls the Civil 3D MCP plugin for the current Windows user (no admin rights needed).
#
#   Install:   Install-Civil3DPlugin.cmd      (or this script with no arguments)
#   Uninstall: Uninstall-Civil3DPlugin.cmd    (or this script with -Uninstall), or Windows Settings > Apps
#
# Install copies Civil3DMcpPlugin.bundle to %APPDATA%\Autodesk\ApplicationPlugins (Civil 3D 2027 loads bundles
# there at startup; the AI connection stays off until the user turns it on) and adds an entry to
# Settings > Apps so it can be uninstalled like any other program. Uninstall removes exactly those two things;
# saved scripts and logs are kept.
param([switch]$Uninstall)

$ErrorActionPreference = 'Stop'
$bundleName = 'Civil3DMcpPlugin.bundle'
$pluginsRoot = Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins'
$target = Join-Path $pluginsRoot $bundleName
$appsKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Civil3DMcpPlugin'
# Permanent copy of the MCP server used by Claude Desktop (so deleting the extracted zip doesn't break it).
$serverHome = Join-Path $env:LOCALAPPDATA 'civil3d-mcp'

function Get-Node {
  $node = Get-Command node -ErrorAction SilentlyContinue
  if (-not $node) { return $null }
  $major = [int](((& $node.Source --version) -replace '^v', '') -split '\.')[0]
  if ($major -lt 18) { return $null }
  return $node.Source
}

function Wait-ClaudeDesktopClosed {
  # Claude Desktop rewrites its settings file while running, so it must be closed when we change it.
  for ($attempt = 1; Get-Process -Name 'Claude' -ErrorAction SilentlyContinue; $attempt++) {
    if ($attempt -gt 5) {
      Write-Host 'Claude Desktop is still open; skipped adding the Civil 3D tools. Run the installer again after quitting it.' -ForegroundColor Yellow
      return $false
    }
    Write-Host 'Claude Desktop is open. Quit it completely (right-click its taskbar/tray icon > Quit), then press Enter.' -ForegroundColor Yellow
    Write-Host '(Type skip and press Enter to skip the Claude Desktop step.)'
    if ((Read-Host) -eq 'skip') { return $false }
  }
  return $true
}

function Invoke-ClaudeConfig([string[]]$arguments) {
  $helper = Join-Path $PSScriptRoot 'claude-desktop-config.mjs'
  $node = Get-Node
  if (-not $node -or -not (Test-Path -LiteralPath $helper)) { return $false }
  & $node $helper @arguments
  return ($LASTEXITCODE -eq 0)
}

function Remove-Bundle {
  if (-not (Test-Path -LiteralPath $target)) { return }
  try {
    Remove-Item -LiteralPath $target -Recurse -Force
  }
  catch {
    Write-Host "Could not remove $target because Civil 3D is using it." -ForegroundColor Yellow
    Write-Host 'Close every Civil 3D window, then run this again.' -ForegroundColor Yellow
    exit 1
  }
}

if ($Uninstall) {
  $wasInstalled = (Test-Path -LiteralPath $target) -or (Test-Path $appsKey) -or (Test-Path -LiteralPath $serverHome)
  if (Test-Path -LiteralPath $serverHome) {
    if (Get-Process -Name 'Claude' -ErrorAction SilentlyContinue) {
      Write-Host 'Note: quit and reopen Claude Desktop afterwards so it drops the Civil 3D tools.' -ForegroundColor Yellow
    }
    if (-not (Invoke-ClaudeConfig @('remove'))) {
      Write-Host 'Could not update Claude Desktop settings automatically (Node.js not found).' -ForegroundColor Yellow
      Write-Host 'Remove "civil3d-mcp" from claude_desktop_config.json by hand if Claude Desktop still lists it.'
    }
    Remove-Item -LiteralPath $serverHome -Recurse -Force -ErrorAction SilentlyContinue
  }
  Remove-Bundle
  Remove-Item -Path $appsKey -Recurse -Force -ErrorAction SilentlyContinue
  if ($wasInstalled) {
    Write-Host 'Civil 3D MCP plugin uninstalled.' -ForegroundColor Green
  }
  else {
    Write-Host 'The Civil 3D MCP plugin was not installed; nothing to remove.'
  }
  Write-Host 'Kept (delete by hand if you no longer want them):'
  Write-Host "  Saved scripts: $([Environment]::GetFolderPath('MyDocuments'))\Civil3DMcp\scripts"
  Write-Host "  Plugin log:    $env:LOCALAPPDATA\Civil3DMcpPlugin"
  exit 0
}

# ---------- install ----------
$source = Join-Path $PSScriptRoot $bundleName
$manifest = Join-Path $source 'PackageContents.xml'
if (-not (Test-Path -LiteralPath $manifest)) {
  Write-Host "Can't find $bundleName next to this script. Extract the whole release zip first, then run it from there." -ForegroundColor Red
  exit 1
}
$version = ([xml](Get-Content -LiteralPath $manifest -Raw)).ApplicationPackage.AppVersion

# Civil 3D 2027 registers its product under AutoCAD release R26.0.
$civil2027 = Get-ChildItem 'HKLM:\SOFTWARE\Autodesk\AutoCAD\R26.0' -ErrorAction SilentlyContinue |
  Where-Object { (Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue).ProductName -like '*Civil 3D 2027*' }
if (-not $civil2027) {
  Write-Host 'Warning: Autodesk Civil 3D 2027 was not found on this PC. The plugin only works in Civil 3D 2027.' -ForegroundColor Yellow
}

New-Item -ItemType Directory -Force -Path $pluginsRoot | Out-Null
Remove-Bundle
Copy-Item -LiteralPath $source -Destination $target -Recurse
# Keep a copy of this script with the plugin so Settings > Apps can uninstall it later.
$uninstallDir = Join-Path $target 'Uninstall'
New-Item -ItemType Directory -Force -Path $uninstallDir | Out-Null
Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $uninstallDir 'Install-Civil3DPlugin.ps1')
$configHelper = Join-Path $PSScriptRoot 'claude-desktop-config.mjs'
if (Test-Path -LiteralPath $configHelper) { Copy-Item -LiteralPath $configHelper -Destination $uninstallDir }
# Files from a downloaded zip are marked as coming from the internet; unblock so Civil 3D can load the DLLs.
Get-ChildItem -LiteralPath $target -Recurse -File | Unblock-File

New-Item -Path $appsKey -Force | Out-Null
$uninstallCommand = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$uninstallDir\Install-Civil3DPlugin.ps1`" -Uninstall"
$entries = @{
  DisplayName     = 'Civil 3D MCP plugin (civil3d-mcp-unified)'
  DisplayVersion  = $version
  Publisher       = 'civil3d-mcp-unified'
  InstallLocation = $target
  UninstallString = $uninstallCommand
  QuietUninstallString = $uninstallCommand
  Comments        = 'Connects AI assistants to Civil 3D 2027. Tested only with Civil 3D 2027 (English) build 13.9.1308.'
}
foreach ($name in $entries.Keys) { New-ItemProperty -Path $appsKey -Name $name -Value $entries[$name] -PropertyType String -Force | Out-Null }
New-ItemProperty -Path $appsKey -Name NoModify -Value 1 -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $appsKey -Name NoRepair -Value 1 -PropertyType DWord -Force | Out-Null

Write-Host "Civil 3D MCP plugin $version installed to $target" -ForegroundColor Green

# ---------- optional: Claude Desktop ----------
$claudeSettings = @(Join-Path $env:APPDATA 'Claude') +
  @(Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Packages') -Directory -Filter 'Claude_*' -ErrorAction SilentlyContinue |
    ForEach-Object { Join-Path $_.FullName 'LocalCache\Roaming\Claude' }) | Where-Object { Test-Path -LiteralPath $_ }
$serverSource = Join-Path $PSScriptRoot 'mcp-server'
if ($claudeSettings -and (Test-Path -LiteralPath $serverSource)) {
  Write-Host ''
  $answer = Read-Host 'Add the Civil 3D tools to Claude Desktop now? [Y/n]'
  if ($answer -notmatch '^(n|no)$') {
    if (-not (Get-Node)) {
      Write-Host 'Node.js 18 or newer is needed for this step and was not found.' -ForegroundColor Yellow
      Write-Host 'Install it from https://nodejs.org (LTS) and run this installer again, or in Claude Desktop use'
      Write-Host 'Settings > Extensions > Advanced settings > Install Extension and pick the .mcpb file from this zip.'
    }
    elseif (Wait-ClaudeDesktopClosed) {
      if (Test-Path -LiteralPath $serverHome) { Remove-Item -LiteralPath $serverHome -Recurse -Force }
      New-Item -ItemType Directory -Force -Path $serverHome | Out-Null
      Copy-Item -LiteralPath $serverSource -Destination (Join-Path $serverHome 'mcp-server') -Recurse
      Get-ChildItem -LiteralPath $serverHome -Recurse -File | Unblock-File
      if (Invoke-ClaudeConfig @('add', (Join-Path $serverHome 'mcp-server\server\index.js'))) {
        Write-Host 'Open Claude Desktop: the Civil 3D tools are listed under its connectors/tools.' -ForegroundColor Green
      }
    }
  }
}

Write-Host ''
Write-Host 'Start (or restart) Civil 3D 2027 and look for the Civil 3D MCP ribbon tab.'
Write-Host 'The AI connection is OFF until you turn it on there (or with C3DMCPSTART).'
Write-Host 'If Civil 3D asks whether to load the plugin, choose Always Load.'
Write-Host 'To remove it: Uninstall-Civil3DPlugin.cmd, or Windows Settings > Apps > Installed apps.'
