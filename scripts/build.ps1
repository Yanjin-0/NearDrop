param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Set-Location $projectRoot
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.build'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:MSBuildEnableWorkloadResolver = 'false'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.build\packages'
function Invoke-Dotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $Arguments" }
}
& (Join-Path $PSScriptRoot 'prepare-engine.ps1')
Invoke-Dotnet @('build','NearDrop.sln','-c','Release','-m:1','-p:UseSharedCompilation=false','-v','minimal')
if (!$SkipTests) {
    & (Join-Path $PSScriptRoot 'test.ps1')
    Invoke-Dotnet @('tests\Relay.UiTests\bin\Release\net10.0-windows\Relay.UiTests.dll',$projectRoot)
}
$output = Join-Path $projectRoot 'artifacts\app'
Invoke-Dotnet @('publish','src\Relay.App','-c','Release','-r','win-x64','--self-contained','true','-o',$output,'-m:1','-p:UseSharedCompilation=false','-v','minimal')
# ZIP is only an internal installer payload; friends receive the single setup EXE.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$payload = Join-Path $projectRoot 'artifacts\payload.zip'
if (Test-Path $payload) { Remove-Item -LiteralPath $payload }
[IO.Compression.ZipFile]::CreateFromDirectory($output, $payload, [IO.Compression.CompressionLevel]::Optimal, $false)
(Get-FileHash $payload -Algorithm SHA256).Hash | Set-Content (Join-Path $projectRoot 'artifacts\payload.sha256') -Encoding ascii
Invoke-Dotnet @('publish','src\Relay.Setup','-c','Release','-r','win-x64','--self-contained','true','-o','artifacts\setup','-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true','-p:EnableCompressionInSingleFile=true','-m:1','-p:UseSharedCompilation=false','-v','minimal')
$version = ([xml](Get-Content -Raw (Join-Path $projectRoot 'Directory.Build.props'))).Project.PropertyGroup.Version
$setup = Join-Path $projectRoot "artifacts\NearDrop-$version-win-x64-Setup.exe"
Copy-Item -LiteralPath (Join-Path $projectRoot 'artifacts\setup\NearDrop-Setup.exe') -Destination $setup -Force
$release = [ordered]@{ product='NearDrop'; version=$version; runtime='10.0.15'; architecture='win-x64'; syncthing='2.1.7'; setupSha256=(Get-FileHash $setup).Hash; built=(Get-Date -Format o); signed=$false }
$release | ConvertTo-Json | Set-Content (Join-Path $projectRoot 'artifacts\release.json') -Encoding utf8
Write-Output "Installer: $setup"
