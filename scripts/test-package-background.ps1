$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$release = Get-Content (Join-Path $projectRoot 'artifacts\release.json') -Raw | ConvertFrom-Json
$setup = Join-Path $projectRoot "artifacts\NearDrop-$($release.version)-win-x64-Setup.exe"
if ((Get-FileHash -LiteralPath $setup).Hash -ne $release.setupSha256) { throw 'Setup digest mismatch.' }
$run = Join-Path $projectRoot ('test-results\package-background-' + [guid]::NewGuid().ToString('N'))
$install = Join-Path $run 'installed'
$report = Join-Path $run 'error.txt'
New-Item -Path $run -ItemType Directory | Out-Null

function Invoke-Setup([string]$Executable, [string[]]$Actions) {
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    $start.WindowStyle = 'Hidden'
    foreach ($argument in (@('--silent','--isolated','--install-dir',$install,'--result',$report) + $Actions)) {
        $start.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (!$process.WaitForExit(60000)) { throw 'Setup timed out.' }
        if ($process.ExitCode -ne 0) { throw "Setup failed with exit code $($process.ExitCode)." }
    }
    finally { if (!$process.HasExited) { $process.Kill(); $process.WaitForExit() } }
}

Invoke-Setup $setup @()
foreach ($relative in @('NearDrop.exe','hostfxr.dll','coreclr.dll','engine\syncthing.exe','engine\LICENSE.txt','docs\TESTING.md')) {
    if (!(Test-Path -LiteralPath (Join-Path $install $relative))) { throw "Missing $relative." }
}
$engine = Join-Path $install 'engine\syncthing.exe'
$lock = Get-Content (Join-Path $projectRoot 'vendor\syncthing.lock.json') -Raw | ConvertFrom-Json
if ((Get-FileHash -LiteralPath $engine).Hash -ne $lock.executableSha256) { throw 'Installed engine digest mismatch.' }
$sentinel = Join-Path $install '未列入安裝清單的檔案.txt'
Set-Content -LiteralPath $sentinel -Value 'preserve on uninstall'
Invoke-Setup (Join-Path $install 'Uninstall.exe') @('--uninstall')
$record = Join-Path $install 'neardrop-install.json'
for ($attempt = 0; $attempt -lt 40 -and (Test-Path -LiteralPath $record); $attempt++) { Start-Sleep -Milliseconds 500 }
if (Test-Path -LiteralPath $report) { Get-Content -LiteralPath $report; throw 'Uninstaller error.' }
if ((Test-Path -LiteralPath $record) -or (Test-Path -LiteralPath $engine) -or !(Test-Path -LiteralPath $sentinel)) { throw 'Unexpected uninstall result.' }
'PASS silent isolated install and uninstall verified; no interactive desktop application was launched' | Tee-Object -FilePath (Join-Path $run 'results.txt')
Write-Output "Test artifacts: $run"
