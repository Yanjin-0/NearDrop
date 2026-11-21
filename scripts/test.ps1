param([switch]$Stress, [switch]$LargeFile)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Set-Location $projectRoot
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.build'
$env:MSBuildEnableWorkloadResolver = 'false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
dotnet build tests\Relay.Tests -c Release -m:1 -p:UseSharedCompilation=false -v minimal
if ($LASTEXITCODE -ne 0) { throw 'Test build failed' }
$testArgs = @($projectRoot)
if ($Stress) { $testArgs += '--stress' }
if ($LargeFile) { $testArgs += '--large-file' }
dotnet tests\Relay.Tests\bin\Release\net10.0-windows\Relay.Tests.dll @testArgs
if ($LASTEXITCODE -ne 0) { throw 'Integration tests failed' }
