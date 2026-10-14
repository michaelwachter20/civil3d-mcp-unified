# Installs (or with -Uninstall removes) the Civil 3D MCP plugin as an Autodesk application bundle.
# Civil 3D 2027 loads bundles in %APPDATA%\Autodesk\ApplicationPlugins automatically at startup,
# so no NETLOAD is needed afterwards. Only touches the Civil3DMcpPlugin.bundle folder.
param([switch]$Uninstall)

$ErrorActionPreference = 'Stop'
$bundleName = 'Civil3DMcpPlugin.bundle'
$source = Join-Path $PSScriptRoot $bundleName
$pluginsRoot = Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins'
$target = Join-Path $pluginsRoot $bundleName

function Remove-Bundle {
  if (Test-Path $target) {
    try {
      Remove-Item -LiteralPath $target -Recurse -Force
    }
    catch {
      Write-Host "Could not replace $target because a file is in use." -ForegroundColor Yellow
      Write-Host 'Close every Civil 3D window that has the plugin loaded, then run this again.' -ForegroundColor Yellow
      exit 1
    }
  }
}

if ($Uninstall) {
  Remove-Bundle
  Write-Host "Removed $target. Restart Civil 3D to unload the plugin." -ForegroundColor Green
  exit 0
}

if (-not (Test-Path (Join-Path $source 'PackageContents.xml'))) {
  Write-Host "Can't find $bundleName next to this script. Extract the whole release zip first, then run it from there." -ForegroundColor Red
  exit 1
}

New-Item -ItemType Directory -Force -Path $pluginsRoot | Out-Null
Remove-Bundle
Copy-Item -LiteralPath $source -Destination $target -Recurse
# Files from a downloaded zip are marked as coming from the internet; unblock so Civil 3D can load the DLLs.
Get-ChildItem -LiteralPath $target -Recurse -File | Unblock-File

Write-Host "Installed to $target" -ForegroundColor Green
Write-Host 'Start (or restart) Civil 3D 2027. The plugin loads automatically and listens on port 8080.'
Write-Host 'Check it with the C3DMCPSTATUS command. If Civil 3D asks whether to load it, choose Always Load.'
