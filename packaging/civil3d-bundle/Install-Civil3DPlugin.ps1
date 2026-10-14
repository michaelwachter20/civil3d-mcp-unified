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
  $wasInstalled = (Test-Path -LiteralPath $target) -or (Test-Path $appsKey)
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
Write-Host 'Start (or restart) Civil 3D 2027 and look for the Civil 3D MCP ribbon tab.'
Write-Host 'The AI connection is OFF until you turn it on there (or with C3DMCPSTART).'
Write-Host 'If Civil 3D asks whether to load the plugin, choose Always Load.'
Write-Host 'To remove it: Uninstall-Civil3DPlugin.cmd, or Windows Settings > Apps > Installed apps.'
