# WARUDO Agent Bridge installer for Windows.
#
# One line (PowerShell):
#   irm https://raw.githubusercontent.com/StudioRaming/warudo-agent-bridge/main/install.ps1 | iex
#
# With options, save the script first and run it:
#   .\install.ps1 -Clients claude-code,codex -Warudo "D:\SteamLibrary\steamapps\common\Warudo"
# or set them as environment variables before the one-liner:
#   $env:WARUDO_AGENT_BRIDGE_CLIENTS = "claude-code,codex"; irm ... | iex
#
# The script only makes sure Node.js 20+ is present, then runs
#   npx --yes @studioraming/warudo-agent-bridge@latest install
# which copies the bridge to %USERPROFILE%\.warudo-agent-bridge, installs the WARUDO runtime and
# registers the MCP server with the AI clients it finds (backups are kept for every file it edits).
# It never calls exit, so the PowerShell window stays open when run with iex.

function Install-WarudoAgentBridge {
  param(
    [string]$Clients = $env:WARUDO_AGENT_BRIDGE_CLIENTS,
    [string]$Warudo = $env:WARUDO_PATH,
    [string]$Source = $env:WARUDO_AGENT_BRIDGE_SOURCE,
    [switch]$SkipRuntime
  )
  if (-not $Source) { $Source = '@studioraming/warudo-agent-bridge@latest' }

  function Get-NodeMajor {
    $node = Get-Command node -ErrorAction SilentlyContinue
    if (-not $node) { return 0 }
    $version = (& $node.Source --version) 2>$null
    if ($version -match '^v(\d+)\.') { return [int]$Matches[1] }
    return 0
  }

  function Update-SessionPath {
    $machine = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    $user = [Environment]::GetEnvironmentVariable('Path', 'User')
    $env:Path = (@($machine, $user) | Where-Object { $_ }) -join ';'
  }

  Write-Host 'WARUDO Agent Bridge installer' -ForegroundColor Cyan

  $major = Get-NodeMajor
  if ($major -lt 20) {
    if ($major -gt 0) { Write-Host "Node.js $major found; version 20 or newer is needed." -ForegroundColor Yellow }
    else { Write-Host 'Node.js was not found; version 20 or newer is needed.' -ForegroundColor Yellow }
    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if (-not $winget) {
      Write-Host 'Install Node.js LTS from https://nodejs.org, open a new PowerShell window and run this installer again.' -ForegroundColor Yellow
      return
    }
    $answer = Read-Host 'Install Node.js LTS now with winget? [Y/n]'
    if ($answer -and $answer -notmatch '^(y|yes)$') {
      Write-Host 'Stopped. Install Node.js 20+ and run the installer again.' -ForegroundColor Yellow
      return
    }
    & $winget.Source install --id OpenJS.NodeJS.LTS --exact --accept-source-agreements --accept-package-agreements
    Update-SessionPath
    $major = Get-NodeMajor
    if ($major -lt 20) {
      Write-Host 'Node.js is installed but not visible in this window yet. Open a new PowerShell window and run the installer again.' -ForegroundColor Yellow
      return
    }
  }

  $arguments = @('--yes', $Source, 'install')
  if ($Clients) { $arguments += @('--clients', $Clients) }
  if ($Warudo) { $arguments += @('--warudo', $Warudo) }
  if ($SkipRuntime) { $arguments += '--skip-runtime' }

  Write-Host "Running: npx $($arguments -join ' ')" -ForegroundColor DarkGray
  & npx @arguments
  if ($LASTEXITCODE -ne 0) {
    Write-Host "The installer stopped with exit code $LASTEXITCODE. Details are above; 'npx --yes $Source doctor' checks the setup." -ForegroundColor Red
    return
  }
  Write-Host ''
  Write-Host 'Done. Restart your AI client (Claude Code, Codex, Claude Desktop or Cursor) and ask it to call warudo_status.' -ForegroundColor Green
}

Install-WarudoAgentBridge @args
