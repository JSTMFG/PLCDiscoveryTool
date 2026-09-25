$ErrorActionPreference = 'Stop'
$agentPath = Join-Path $PSScriptRoot 'JST Echo Reset Agent.exe'

if (-not (Test-Path -LiteralPath $agentPath)) {
    Write-Error 'JST Echo Reset Agent.exe is missing from this folder.'
    exit 1
}

Write-Host 'Starting JST Echo Reset Agent for 10.10.10.200...' -ForegroundColor Cyan
Write-Host 'Keep this window open. You may disconnect Remote Desktop, but do not sign out.'
Write-Host 'Press Ctrl+C to stop the agent.'
Write-Host

& $agentPath --portable --host 10.10.10.200
exit $LASTEXITCODE
