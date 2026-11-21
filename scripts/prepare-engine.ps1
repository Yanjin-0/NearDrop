$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$lock = Get-Content (Join-Path $projectRoot 'vendor\syncthing.lock.json') -Raw | ConvertFrom-Json
$archive = Join-Path $projectRoot 'vendor\syncthing.zip'
if (!(Test-Path $archive)) { Invoke-WebRequest $lock.url -OutFile $archive }
if ((Get-FileHash $archive -Algorithm SHA256).Hash -ne $lock.sha256) { throw 'Official Syncthing archive SHA-256 mismatch. No executable will be run.' }
$engineFolder = Join-Path $projectRoot "vendor\syncthing-windows-amd64-v$($lock.version)"
if (!(Test-Path (Join-Path $engineFolder 'syncthing.exe'))) { Expand-Archive $archive (Join-Path $projectRoot 'vendor') -Force }
if ((Get-FileHash (Join-Path $engineFolder 'syncthing.exe') -Algorithm SHA256).Hash -ne $lock.executableSha256) { throw 'Syncthing executable SHA-256 mismatch.' }
$signature = Get-AuthenticodeSignature (Join-Path $engineFolder 'syncthing.exe')
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $lock.signerThumbprint) { throw 'Official Syncthing Authenticode signature did not validate.' }
Write-Output "Verified official Syncthing $($lock.version)"
