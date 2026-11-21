$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$run = Join-Path $projectRoot ('test-results\incoming-ui-' + [guid]::NewGuid().ToString('N'))
$program = Join-Path $projectRoot 'artifacts\app\NearDrop.exe'
New-Item -Path $run -ItemType Directory | Out-Null
$senderHome = Join-Path $run 'sender'
$receiverHome = Join-Path $run 'receiver'
$source = Join-Path $run '收件通知實傳樣本.txt'
Set-Content -LiteralPath $source -Value 'NearDrop isolated local UI test'

function Start-TestApp([string]$Profile, [string]$File = '') {
    $start = [Diagnostics.ProcessStartInfo]::new($program)
    $start.UseShellExecute = $false
    $start.WindowStyle = 'Hidden'
    foreach ($argument in @('--profile', $Profile, '--local-test')) { $start.ArgumentList.Add($argument) }
    if ($File) { $start.ArgumentList.Add($File) }
    [Diagnostics.Process]::Start($start)
}
function Ui([string[]]$Arguments) {
    & winapp ui @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "winapp did not complete: $($Arguments[0]) $($Arguments[1])" }
}
function Read-Node([string]$Profile, [int]$ProcessId) {
    Ui @('wait-for', 'MyId', '-a', $ProcessId, '--property', 'IsEnabled', '--value', 'true', '--timeout', '15000')
    [xml]$config = Get-Content -LiteralPath (Join-Path $Profile 'engine\config.xml') -Raw
    $address = [string]$config.configuration.gui.address
    $listen = [string]$config.configuration.options.listenAddress
    if ($address -notmatch '^127\.0\.0\.1:\d+$' -or $listen -notmatch '^tcp://127\.0\.0\.1:\d+$') { throw 'Test endpoints must use loopback.' }
    $id = (& winapp ui get-value MyId -a $ProcessId | Out-String).Trim()
    if ($id -notmatch '^(?:[A-Z2-7]{7}-){7}[A-Z2-7]{7}$') { throw 'Missing device identity.' }
    @{ Id = $id; Listen = $listen; Api = "http://$address/rest/"; Headers = @{'X-API-Key' = [string]$config.configuration.gui.apikey} }
}
function Pair-TestNode($Node, $Peer) {
    $device = Invoke-RestMethod ($Node.Api + 'config/defaults/device') -Headers $Node.Headers -NoProxy
    $device.deviceID = $Peer.Id
    $device.name = '本機驗收裝置'
    $device.addresses = @($Peer.Listen)
    $device.introducer = $false
    $device.autoAcceptFolders = $false
    Invoke-RestMethod ($Node.Api + 'config/devices') -Method Post -Headers $Node.Headers -NoProxy -ContentType 'application/json' -Body ($device | ConvertTo-Json -Depth 8) | Out-Null
}
function Stop-TestApp($Process) {
    if ($null -eq $Process -or $Process.HasExited) { return }
    try {
        Ui @('invoke', 'SettingsTab', '-a', $Process.Id)
        Ui @('invoke', '退出並停止背景傳輸', '-a', $Process.Id)
        if (!$Process.WaitForExit(10000)) { $Process.Kill(); $Process.WaitForExit() }
    }
    catch { if (!$Process.HasExited) { $Process.Kill(); $Process.WaitForExit() } }
}

$sender = $null
$receiver = $null
try {
    $sender = Start-TestApp $senderHome
    $a = Read-Node $senderHome $sender.Id
    $receiver = Start-TestApp $receiverHome
    $b = Read-Node $receiverHome $receiver.Id
    Pair-TestNode $a $b
    Pair-TestNode $b $a

    $forward = Start-TestApp $senderHome $source
    if (!$forward.WaitForExit(8000) -or $forward.ExitCode -ne 0) { throw 'Draft forwarding failed.' }
    Ui @('wait-for', 'SendButton', '-a', $sender.Id, '--property', 'IsEnabled', '--value', 'true', '--timeout', '20000')
    Ui @('invoke', 'SendButton', '-a', $sender.Id)
    Ui @('wait-for', 'ReceiveTab', '-a', $receiver.Id, '--property', 'Name', '--value', '接收（1）', '--timeout', '25000')
    Ui @('invoke', 'ReceiveTab', '-a', $receiver.Id)
    Ui @('wait-for', 'IncomingSummary', '-a', $receiver.Id, '--property', 'Name', '--value', '1 筆待接受 · 0 筆待保存', '--timeout', '5000')
    Ui @('invoke', 'NextIncomingButton', '-a', $receiver.Id)
    Ui @('invoke', 'AcceptButton', '-a', $receiver.Id)
    Ui @('wait-for', 'IncomingSummary', '-a', $receiver.Id, '--property', 'Name', '--value', '0 筆待接受 · 1 筆待保存', '--timeout', '25000')
    Ui @('invoke', 'NextIncomingButton', '-a', $receiver.Id)
    Ui @('wait-for', 'SaveButton', '-a', $receiver.Id, '--property', 'IsEnabled', '--value', 'true', '--timeout', '5000')
    Ui @('screenshot', '-a', $receiver.Id, '-o', (Join-Path $run 'awaiting-save.png'))
    Ui @('invoke', 'SaveButton', '-a', $receiver.Id)
    Ui @('wait-for', 'ReceiveTab', '-a', $receiver.Id, '--property', 'Name', '--value', '接收', '--timeout', '25000')

    $state = Get-Content -LiteralPath (Join-Path $receiverHome 'state.json') -Raw | ConvertFrom-Json
    $job = @($state.Jobs)[0]
    $saved = Join-Path $job.SavedPath ('Files\' + [IO.Path]::GetFileName($source))
    if (!$job.Saved -or (Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $saved).Hash) { throw 'Real payload was not saved intact.' }
    if (@($state.AnnouncedIncoming | Where-Object { $_ -like 'offer/*' }).Count -ne 1 -or
        @($state.AnnouncedIncoming | Where-Object { $_ -like 'save/*' }).Count -ne 1) { throw 'Expected one announcement per action stage.' }
    'PASS two local engines: real invite counted, pending-save counted, announcements recorded once per stage and badge cleared after matching save' |
        Tee-Object -FilePath (Join-Path $run 'results.txt')
    Write-Output "Test artifacts: $run"
}
finally {
    Stop-TestApp $receiver
    Stop-TestApp $sender
}
