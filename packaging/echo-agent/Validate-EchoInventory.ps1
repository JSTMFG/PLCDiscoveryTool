$ErrorActionPreference = 'Stop'
$agentPath = Join-Path $PSScriptRoot 'JST Echo Reset Agent.exe'

if (-not (Test-Path -LiteralPath $agentPath)) {
    Write-Error 'JST Echo Reset Agent.exe is missing from this folder.'
    exit 1
}

Write-Host 'Reading every FactoryTalk Logix Echo chassis and controller...' -ForegroundColor Cyan
Write-Host
& $agentPath --validate-sdk --portable --host 10.10.10.200
exit $LASTEXITCODE
