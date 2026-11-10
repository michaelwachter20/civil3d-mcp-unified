# Launcher that Claude Code starts as the stdio MCP server (registered by scripts/install-claude-mcp.ps1).
# On first run it installs npm dependencies and builds the server if node_modules or build/index.js is missing,
# then runs node build/index.js in the foreground and passes its exit code back.
# stdout carries the MCP protocol, so npm's output is discarded (*> $null): any stray text would corrupt the stream.
# Writes only node_modules/ and build/ in this repository; it doesn't start Civil 3D.
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$nodeModulesPath = Join-Path $repoRoot "node_modules"
$buildEntryPath = Join-Path $repoRoot "build/index.js"
$npmCommand = "npm.cmd"
$nodeCommand = "node"

if (-not (Get-Command $nodeCommand -ErrorAction SilentlyContinue)) {
  throw "Node.js was not found on PATH."
}

if (-not (Get-Command $npmCommand -ErrorAction SilentlyContinue)) {
  throw "npm was not found on PATH."
}

if (-not (Test-Path $nodeModulesPath)) {
  & $npmCommand install --no-fund --no-audit --silent --prefix $repoRoot *> $null
  if ($LASTEXITCODE -ne 0) {
    throw "Failed to install npm dependencies for civil3d-mcp."
  }
}

if (-not (Test-Path $buildEntryPath)) {
  & $npmCommand run build --prefix $repoRoot *> $null
  if ($LASTEXITCODE -ne 0) {
    throw "Failed to build civil3d-mcp."
  }
}

& $nodeCommand $buildEntryPath
exit $LASTEXITCODE
