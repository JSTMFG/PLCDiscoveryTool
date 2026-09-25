param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$localDotnet = Join-Path $PSScriptRoot '.tools/dotnet/dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { 'dotnet' }
Push-Location $PSScriptRoot
try {
    if (-not $SkipTests) {
        & $dotnet run --project tests/Jst.PlcFinder.Tests -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    }
    & $dotnet publish src/Jst.PlcFinder/Jst.PlcFinder.csproj -c Release -r win-x64 --self-contained true -o dist
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    $plcExecutable = Join-Path $PSScriptRoot 'dist/JST PLC Finder.exe'
    $plcArchive = Join-Path $PSScriptRoot 'dist/JST PLC Finder.zip'
    Compress-Archive -LiteralPath $plcExecutable -DestinationPath $plcArchive -CompressionLevel Optimal -Force
    $agentOutput = Join-Path $PSScriptRoot 'dist/Echo Agent'
    & $dotnet publish src/Jst.EchoAgent/Jst.EchoAgent.csproj -c Release -r win-x64 --self-contained true -o $agentOutput
    if ($LASTEXITCODE -ne 0) { throw 'Echo agent publish failed.' }
    Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'packaging/echo-agent') -File |
        Where-Object Name -ne 'JST Echo Reset Agent.exe' |
        Copy-Item -Destination $agentOutput -Force
    $agentArchive = Join-Path $PSScriptRoot 'dist/JST Echo Reset Agent - Portable.zip'
    Compress-Archive -Path (Join-Path $agentOutput '*') -DestinationPath $agentArchive -CompressionLevel Optimal -Force
    Write-Host "Executable: $PSScriptRoot\dist\JST PLC Finder.exe"
    Write-Host "Executable ZIP: $PSScriptRoot\dist\JST PLC Finder.zip"
    Write-Host "Echo agent: $agentOutput\JST Echo Reset Agent.exe"
    Write-Host "Echo agent ZIP: $agentArchive"
} finally { Pop-Location }
