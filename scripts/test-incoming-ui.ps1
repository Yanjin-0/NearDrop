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
function Pair-TestNode($Node, $Peer, [string]$Name = '本機驗收裝置') {
    $device = Invoke-RestMethod ($Node.Api + 'config/defaults/device') -Headers $Node.Headers -NoProxy
    $device.deviceID = $Peer.Id
    $device.name = $Name
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
    $offlineId = 'BAAAAAA-AAAAAA7-BAAAAAA-AAAAAA7-BAAAAAA-AAAAAA7-BAAAAAA-AAAAAA7'
    $watchId = 'nd-live-11111111111111111111111111111111'
    $watchSource = (New-Item -ItemType Directory -Path (Join-Path $run '已停止的同步樣本')).FullName
    New-Item -ItemType Directory -Path $senderHome | Out-Null
    @{ Version = 1; Jobs = @(@{
        Id = $watchId; Kind = 'watch-send'; Label = '已停止的同步樣本'; FriendId = $offlineId
        FriendName = '未連線測試裝置'; Path = $watchSource; Detached = $true; State = '已停止共享 · 本機檔案保留'
    }) } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $senderHome 'state.json') -Encoding utf8
    $sender = Start-TestApp $senderHome
    $a = Read-Node $senderHome $sender.Id
    $receiver = Start-TestApp $receiverHome
    $b = Read-Node $receiverHome $receiver.Id
    Pair-TestNode $a $b
    Pair-TestNode $b $a
    Pair-TestNode $a @{ Id = $offlineId; Listen = 'tcp://127.0.0.1:9' } '未連線測試裝置'
    Ui @('invoke', 'SendTab', '-a', $sender.Id)
    Ui @('wait-for', '未連線測試裝置', '--type', 'ListItem', '-a', $sender.Id, '--timeout', '15000')
    Ui @('invoke', 'WatchTab', '-a', $sender.Id)
    Ui @('invoke', 'AddWatchButton', '-a', $sender.Id)
    $pickerHandle = $null
    for ($attempt = 0; $attempt -lt 20 -and !$pickerHandle; $attempt++) {
        $picker = & winapp ui list-windows -a $sender.Id | Select-String 'HWND (\d+).*選取收件朋友' | Select-Object -First 1
        if ($picker) { $pickerHandle = $picker.Matches[0].Groups[1].Value }
        else { Start-Sleep -Milliseconds 250 }
    }
    if (!$pickerHandle) { throw 'Friend picker did not open.' }
    Ui @('wait-for', '選取朋友', '--type', 'Button', '-w', $pickerHandle, '--property', 'IsEnabled', '--value', 'false', '--timeout', '5000')
    Ui @('invoke', '本機驗收裝置', '--type', 'ListItem', '-w', $pickerHandle)
    Ui @('wait-for', '選取朋友', '--type', 'Button', '-w', $pickerHandle, '--property', 'IsEnabled', '--value', 'true', '--timeout', '5000')
    Ui @('invoke', '取消', '--type', 'Button', '-w', $pickerHandle)
    Write-Output 'PASS folder recipient confirmation requires a selection and can be cancelled'
    Ui @('invoke', '已停止的同步樣本', '--root', 'Watches', '--type', 'ListItem', '-a', $sender.Id)
    Ui @('wait-for', 'ToggleWatchButton', '-a', $sender.Id, '--property', 'Name', '--value', '重新啟用共享', '--timeout', '5000')
    Ui @('invoke', 'ToggleWatchButton', '-a', $sender.Id)
    Ui @('wait-for', 'ToggleWatchButton', '-a', $sender.Id, '--property', 'Name', '--value', '暫停', '--timeout', '10000')
    $enabledFolder = Invoke-RestMethod ($a.Api + 'config/folders/' + $watchId) -Headers $a.Headers -NoProxy
    if ($enabledFolder.path -ne $watchSource -or $enabledFolder.paused) { throw 'Re-enabling did not restore the original watch folder.' }
    Write-Output 'PASS stopped automatic folder can re-enable its original share from the watch page'

    $duplicateFolder = (New-Item -ItemType Directory -Path (Join-Path $run 'duplicate')).FullName
    $duplicateFile = Join-Path $duplicateFolder ([IO.Path]::GetFileName($source))
    Set-Content -LiteralPath $duplicateFile -Value 'A separate file with the same name'
    $forward = Start-TestApp $senderHome $source
    if (!$forward.WaitForExit(8000) -or $forward.ExitCode -ne 0) { throw 'Draft forwarding failed.' }
    $duplicate = Start-TestApp $senderHome $duplicateFile
    if (!$duplicate.WaitForExit(8000) -or $duplicate.ExitCode -ne 0) { throw 'Conflicting draft forwarding failed.' }
    Ui @('wait-for', 'SendHint', '-a', $sender.Id, '--property', 'Name', '--value', '請移除其中一個', '--contains', '--timeout', '5000')
    Ui @('wait-for', 'SendButton', '-a', $sender.Id, '--property', 'IsEnabled', '--value', 'false', '--timeout', '5000')
    Ui @('invoke', 'ClearDraftButton', '-a', $sender.Id)
    $forward = Start-TestApp $senderHome $source
    if (!$forward.WaitForExit(8000) -or $forward.ExitCode -ne 0) { throw 'Draft re-selection failed.' }
    Write-Output 'PASS conflicting file names are explained before sending and the draft can be corrected'
    Ui @('invoke', 'HistoryTab', '-a', $sender.Id)
    Ui @('set-value', 'HistorySearch', '原篩選藏起新工作', '-a', $sender.Id)
    Ui @('invoke', 'SendTab', '-a', $sender.Id)
    Ui @('invoke', '本機驗收裝置', '--root', 'SendFriends', '--type', 'ListItem', '-a', $sender.Id)
    Ui @('wait-for', 'SendButton', '-a', $sender.Id, '--property', 'IsEnabled', '--value', 'true', '--timeout', '20000')
    Ui @('invoke', 'SendButton', '-a', $sender.Id)
    Ui @('wait-for', 'StopTransferButton', '-a', $sender.Id, '--property', 'IsEnabled', '--value', 'true', '--timeout', '20000')
    Ui @('wait-for', 'Notice', '-a', $sender.Id, '--property', 'Name', '--value', '已清除原篩選', '--contains', '--timeout', '5000')
    Write-Output 'PASS sending selects the new job despite a previously excluding history filter'
    Ui @('wait-for', 'ReceiveTab', '-a', $receiver.Id, '--property', 'Name', '--value', '接收（1）', '--timeout', '25000')
    Ui @('invoke', 'ReceiveTab', '-a', $receiver.Id)
    Ui @('wait-for', 'IncomingSummary', '-a', $receiver.Id, '--property', 'Name', '--value', '1 筆待接受 · 0 筆待保存', '--timeout', '5000')
    Ui @('invoke', 'NextIncomingButton', '-a', $receiver.Id)
    Ui @('invoke', 'AcceptButton', '-a', $receiver.Id)
    Ui @('wait-for', 'IncomingSummary', '-a', $receiver.Id, '--property', 'Name', '--value', '0 筆待接受 · 1 筆待保存', '--timeout', '25000')
    Ui @('wait-for', 'SaveButton', '-a', $receiver.Id, '--property', 'IsEnabled', '--value', 'true', '--timeout', '5000')
    Write-Output 'PASS accepting an invite retains selection through the pending-save stage'
    Ui @('invoke', 'HistoryTab', '-a', $receiver.Id)
    Ui @('invoke', ([IO.Path]::GetFileName($source)), '--type', 'ListItem', '--root', 'History', '-a', $receiver.Id)
    Ui @('wait-for', 'ViewIncomingButton', '-a', $receiver.Id, '--property', 'Name', '--value', '前往保存', '--timeout', '5000')
    Ui @('invoke', 'ViewIncomingButton', '-a', $receiver.Id)
    Ui @('wait-for', 'SaveButton', '-a', $receiver.Id, '--property', 'IsEnabled', '--value', 'true', '--timeout', '5000')
    Write-Output 'PASS history opens the exact incoming job and exposes its save action'
    Ui @('invoke', 'HistoryTab', '-a', $receiver.Id)
    Ui @('wait-for', 'StopTransferButton', '-a', $receiver.Id, '--property', 'Name', '--value', '停止共享', '--timeout', '5000')
    Ui @('invoke', 'StopTransferButton', '-a', $receiver.Id)
    $stopDialogHandle = $null
    for ($attempt = 0; $attempt -lt 20 -and !$stopDialogHandle; $attempt++) {
        $stopDialog = & winapp ui list-windows -a $receiver.Id | Select-String 'HWND (\d+).*停止共享' | Select-Object -First 1
        if ($stopDialog) { $stopDialogHandle = $stopDialog.Matches[0].Groups[1].Value }
        else { Start-Sleep -Milliseconds 250 }
    }
    if (!$stopDialogHandle) { throw 'Stop share confirmation did not open.' }
    Ui @('invoke', '是', '--type', 'Button', '-w', $stopDialogHandle)
    Ui @('wait-for', 'Notice', '-a', $receiver.Id, '--property', 'Name', '--value', '仍可到接收頁獨立保存', '--contains', '--timeout', '10000')
    Ui @('invoke', 'ViewIncomingButton', '-a', $receiver.Id)
    Ui @('wait-for', 'SaveButton', '-a', $receiver.Id, '--property', 'IsEnabled', '--value', 'true', '--timeout', '5000')
    Ui @('wait-for', 'IncomingSummary', '-a', $receiver.Id, '--property', 'Name', '--value', '0 筆待接受 · 1 筆待保存', '--timeout', '5000')
    Write-Output 'PASS completed receipt remains independently savable after stopping its share'
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
