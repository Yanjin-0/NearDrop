$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$release = Get-Content (Join-Path $projectRoot 'artifacts\release.json') -Raw | ConvertFrom-Json
$setup = Join-Path $projectRoot "artifacts\NearDrop-$($release.version)-win-x64-Setup.exe"
if ((Get-FileHash -LiteralPath $setup).Hash -ne $release.setupSha256) { throw 'Setup digest mismatch.' }
$run = Join-Path $projectRoot ('test-results\setup-' + [guid]::NewGuid().ToString('N'))
$install = Join-Path $run 'installed'
$profile = Join-Path $run 'profile'
$report = Join-Path $run 'error.txt'
New-Item -Path $run -ItemType Directory | Out-Null

function Invoke-Setup([string]$Executable, [string[]]$Actions) {
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    $start.WindowStyle = 'Hidden'
    foreach ($arg in (@('--silent', '--isolated', '--install-dir', $install, '--result', $report) + $Actions)) { $start.ArgumentList.Add($arg) }
    $process = [Diagnostics.Process]::Start($start)
    if (!$process.WaitForExit(60000)) { throw 'Setup timed out.' }
    if ($process.ExitCode -ne 0) {
        if (Test-Path -LiteralPath $report) { Get-Content -LiteralPath $report }
        throw "Setup failed with exit code $($process.ExitCode)."
    }
}
function Assert-UiProperty([string]$Selector, [string]$Property, [string]$Expected, [int]$ProcessId) {
    & winapp ui wait-for $Selector -a $ProcessId --property $Property --value $Expected --timeout 5000 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "$Selector did not report $Property = $Expected." }
}
function Forward-Fixture([string]$Path) {
    $forward = [Diagnostics.ProcessStartInfo]::new((Join-Path $install 'NearDrop.exe'))
    $forward.UseShellExecute = $false
    foreach ($arg in @('--profile', $profile, '--local-test', $Path)) { $forward.ArgumentList.Add($arg) }
    $child = [Diagnostics.Process]::Start($forward)
    if (!$child.WaitForExit(8000) -or $child.ExitCode -ne 0) { throw 'Selection did not reach the running app.' }
}

Invoke-Setup $setup @()
$engine = Join-Path $install 'engine\syncthing.exe'
$lock = Get-Content (Join-Path $projectRoot 'vendor\syncthing.lock.json') -Raw | ConvertFrom-Json
if ((Get-FileHash -LiteralPath $engine).Hash -ne $lock.executableSha256) { throw 'Installed engine digest mismatch.' }
foreach ($relative in @('NearDrop.exe', 'hostfxr.dll', 'coreclr.dll', 'engine\LICENSE.txt', 'docs\TESTING.md', 'docs\WPF-THIRD-PARTY.txt', 'docs\WINFORMS-THIRD-PARTY.txt')) {
    if (!(Test-Path -LiteralPath (Join-Path $install $relative))) { throw "Missing $relative." }
}
Write-Output 'PASS single-file install includes verified engine, runtime and notices'

$start = [Diagnostics.ProcessStartInfo]::new((Join-Path $install 'NearDrop.exe'))
$start.UseShellExecute = $false
$start.WindowStyle = 'Hidden'
$start.Environment['DOTNET_ROOT'] = Join-Path $run 'intentionally-absent-runtime'
$start.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
foreach ($arg in @('--profile', $profile, '--local-test')) { $start.ArgumentList.Add($arg) }
# These display fixtures share neither a real path nor a real device with the test engine.
New-Item -Path $profile -ItemType Directory | Out-Null
$jobs = @(
    @{Id='nd-once-11111111111111111111111111111111'; Kind='receive'; Label='驗收樣本待保存'; FriendName='測試朋友'; State='已接收 · 等待獨立保存'; TotalBytes=1024; DoneBytes=1024; Path=(Join-Path $profile 'fixture-pending')},
    @{Id='nd-once-22222222222222222222222222222222'; Kind='receive'; Label='驗收樣本已保存'; FriendName='測試朋友'; State='已獨立保存'; Saved=$true; Detached=$true; TotalBytes=1024; DoneBytes=1024; Path=(Join-Path $profile 'fixture-completed'); SavedPath=(Join-Path $profile 'fixture-saved')}
)
@{Version=1; Jobs=$jobs} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $profile 'state.json') -Encoding utf8
$app = [Diagnostics.Process]::Start($start)
try {
    & winapp ui wait-for MyId -a $app.Id --timeout 15000 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'No app window or MyId automation element.' }
    $id = ''
    for ($attempt = 0; $attempt -lt 12; $attempt++) {
        $id = (& winapp ui get-value MyId -a $app.Id | Out-String).Trim()
        if ($id -match '^(?:[A-Z2-7]{7}-){7}[A-Z2-7]{7}$') { break }
        Start-Sleep -Milliseconds 500
    }
    if ($id -notmatch '^(?:[A-Z2-7]{7}-){7}[A-Z2-7]{7}$') { throw 'Device identity was not initialized.' }
    Assert-UiProperty MyId IsEnabled true $app.Id
    $core = $app.Modules | Where-Object { $_.ModuleName -eq 'coreclr.dll' }
    if ($core.FileName -ne (Join-Path $install 'coreclr.dll')) { throw 'Runtime was not loaded from the installed payload.' }
    Write-Output 'PASS installed app loads bundled runtime and creates a device identity'
    Assert-UiProperty FriendName Name '朋友名稱' $app.Id
    Assert-UiProperty FriendId Name '朋友的完整裝置 ID' $app.Id
    Assert-UiProperty AddFriendButton IsEnabled false $app.Id
    Write-Output 'PASS pairing fields have accessible names and incomplete pairing is disabled'
    & winapp ui invoke SendTab -a $app.Id
    if ($LASTEXITCODE -ne 0) { throw 'Cannot open send tab.' }
    $button = & winapp ui get-property SendButton -a $app.Id --property IsOffscreen | Out-String
    if ($LASTEXITCODE -ne 0 -or $button -notmatch '(?i)false') { throw 'Send button is offscreen.' }
    Assert-UiProperty SendButton IsEnabled false $app.Id
    Assert-UiProperty SendHint Name '先選取檔案或資料夾。' $app.Id
    & winapp ui invoke NoFriends -a $app.Id
    Assert-UiProperty FriendName HasKeyboardFocus true $app.Id
    & winapp ui invoke SendTab -a $app.Id
    Write-Output 'PASS empty send is disabled and add friend leads directly to pairing'
    $first = Join-Path $run '驗收樣本甲.txt'
    $second = Join-Path $run '驗收樣本乙.txt'
    Set-Content -LiteralPath $first -Value 'one'
    Set-Content -LiteralPath $second -Value 'two'
    Forward-Fixture $first
    Forward-Fixture $second
    Forward-Fixture $first
    & winapp ui wait-for SelectionSummary -a $app.Id --property Name --value '2 個項目' --contains --timeout 5000
    if ($LASTEXITCODE -ne 0) { throw 'Selection did not append or exclude duplicates.' }
    & winapp ui screenshot -a $app.Id -o (Join-Path $run 'draft.png')
    & winapp ui invoke '驗收樣本甲.txt' --type ListItem -a $app.Id
    & winapp ui invoke RemoveDraftButton -a $app.Id
    & winapp ui wait-for SelectionSummary -a $app.Id --property Name --value '1 個項目' --contains --timeout 5000
    if ($LASTEXITCODE -ne 0) { throw 'Selected item was not removed.' }
    & winapp ui invoke ClearDraftButton -a $app.Id
    Assert-UiProperty SelectionSummary Name '尚未選取檔案' $app.Id
    if (!(Test-Path -LiteralPath $first) -or !(Test-Path -LiteralPath $second)) { throw 'Draft editing changed source files.' }
    Write-Output 'PASS draft appends, deduplicates, removes and clears without changing source files'
    & winapp ui screenshot -a $app.Id -o (Join-Path $run 'send.png')
    & winapp ui invoke '傳輸紀錄' --type TabItem -a $app.Id
    if ($LASTEXITCODE -ne 0) { throw 'Could not open transfer history.' }
    Assert-UiProperty ClearOutboxButton IsEnabled false $app.Id
    Assert-UiProperty StopTransferButton IsEnabled false $app.Id
    Write-Output 'PASS history actions are disabled without a selected transfer'
    & winapp ui invoke '設定' --type TabItem -a $app.Id
    & winapp ui invoke ExplorerHotkey -a $app.Id
    if ($LASTEXITCODE -ne 0) { throw 'Could not enable the Explorer shortcut.' }
    & winapp ui wait-for Notice -a $app.Id --property Name --value '已啟用 Ctrl+Alt+N' --contains --timeout 5000
    if ($LASTEXITCODE -ne 0) { throw 'Explorer shortcut did not register.' }
    & winapp ui invoke ReceiveTab -a $app.Id
    & winapp ui wait-for Notice -a $app.Id --gone --timeout 5000 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Settings message leaked onto the receive page.' }
    & winapp ui wait-for AcceptButton -a $app.Id --gone --timeout 5000 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Accept was visible without an incoming offer.' }
    Assert-UiProperty ReceiveTab Name '接收（1）' $app.Id
    Assert-UiProperty IncomingSummary Name '0 筆待接受 · 1 筆待保存' $app.Id
    & winapp ui invoke NextIncomingButton -a $app.Id
    if ($LASTEXITCODE -ne 0) { throw 'Could not open the next actionable receipt.' }
    Assert-UiProperty SaveButton IsEnabled true $app.Id
    $settingsFile = Join-Path $profile 'state.json'
    $announced = (Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json).AnnouncedIncoming
    if (@($announced).Count -ne 1 -or $announced[0] -notlike 'save/*/nd-once-11111111111111111111111111111111') {
        throw 'Only the pending-save fixture should be announced.'
    }
    Write-Output 'PASS receiving badge, actionable navigation, and persistent announcement tracking'
    & winapp ui invoke '驗收樣本待保存' --type ListItem -a $app.Id
    if ($LASTEXITCODE -ne 0) { throw 'Could not select the pending-save fixture.' }
    Assert-UiProperty SaveButton IsEnabled true $app.Id
    & winapp ui invoke '驗收樣本已保存' --type ListItem -a $app.Id
    if ($LASTEXITCODE -ne 0) { throw 'Could not select the saved fixture.' }
    Assert-UiProperty OpenReceivedButton IsEnabled true $app.Id
    & winapp ui wait-for SaveButton -a $app.Id --gone --timeout 5000 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Save was visible on an already saved item.' }
    & winapp ui screenshot -a $app.Id -o (Join-Path $run 'receive.png')
    & winapp ui invoke HistoryTab -a $app.Id
    & winapp ui invoke '驗收樣本待保存' --type ListItem -a $app.Id
    Assert-UiProperty StopTransferButton Name '停止這次接收' $app.Id
    & winapp ui invoke StopTransferButton -a $app.Id
    if ($LASTEXITCODE -ne 0) { throw 'Could not open the stop-transfer confirmation.' }
    $dialog = $null
    for ($attempt = 0; $attempt -lt 10 -and !$dialog; $attempt++) {
        $dialog = & winapp ui list-windows -a $app.Id | Select-String 'HWND (\d+).*停止這次接收' | Select-Object -First 1
        if (!$dialog) { Start-Sleep -Milliseconds 200 }
    }
    if (!$dialog) { throw 'Stop confirmation dialog was not displayed.' }
    & winapp ui invoke '否' --type Button -w $dialog.Matches[0].Groups[1].Value
    if ($LASTEXITCODE -ne 0) { throw 'Could not decline the stop confirmation.' }
    Assert-UiProperty StopTransferButton IsEnabled true $app.Id
    Write-Output 'PASS stopping an active transfer requires confirmation and declining preserves it'
    & winapp ui invoke SettingsTab -a $app.Id
    Write-Output 'PASS page notices are isolated and receive actions match the selected state'
    & winapp ui invoke ReceiveNotifications -a $app.Id
    Assert-UiProperty Notice Name '已關閉收件通知。待處理項目仍顯示在接收頁。' $app.Id
    & winapp ui invoke ReceiveTab -a $app.Id
    Assert-UiProperty ReceiveTab Name '接收（1）' $app.Id
    & winapp ui invoke SettingsTab -a $app.Id
    & winapp ui invoke '退出並停止背景傳輸' -a $app.Id
    if (!$app.WaitForExit(15000)) { throw 'App did not exit normally.' }
    $app = [Diagnostics.Process]::Start($start)
    & winapp ui wait-for MyId -a $app.Id --timeout 15000 | Out-Null
    Assert-UiProperty MyId IsEnabled true $app.Id
    & winapp ui invoke SettingsTab -a $app.Id
    & winapp ui wait-for Notice -a $app.Id --property Name --value '已啟用 Ctrl+Alt+N' --contains --timeout 15000
    if ($LASTEXITCODE -ne 0) { throw 'Explorer shortcut was not restored after restart.' }
    Write-Output 'PASS Explorer shortcut preference is restored and registers after restart'
    if ((Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json).ReceiveNotifications -ne $false) { throw 'Notification preference was lost.' }
    Write-Output 'PASS notification preference survives restart and does not hide the in-app badge'
    & winapp ui invoke '設定' --type TabItem -a $app.Id
    & winapp ui invoke '退出並停止背景傳輸' -a $app.Id
    if (!$app.WaitForExit(15000)) { throw 'App did not exit normally after restart.' }
}
finally { if (!$app.HasExited) { $app.Kill(); $app.WaitForExit() } }

$received = Join-Path $profile 'Received\收件.txt'
$unexpected = Join-Path $install '使用者備份.txt'
Set-Content -LiteralPath $received -Value 'received file'
Set-Content -LiteralPath $unexpected -Value 'unowned backup'
$cert = Join-Path $profile 'engine\cert.pem'
$certificateHash = (Get-FileHash -LiteralPath $cert).Hash
Invoke-Setup (Join-Path $install 'Uninstall.exe') @('--uninstall')
$uninstallRecord = Join-Path $install 'neardrop-install.json'
for ($attempt = 0; $attempt -lt 40 -and (Test-Path -LiteralPath $uninstallRecord); $attempt++) { Start-Sleep -Milliseconds 500 }
if (Test-Path -LiteralPath $report) { Get-Content -LiteralPath $report; throw 'Uninstaller error.' }
if (Test-Path -LiteralPath $uninstallRecord) { throw 'Uninstall did not finish.' }
if ((Test-Path -LiteralPath $engine) -or (Test-Path -LiteralPath (Join-Path $install 'NearDrop.exe'))) { throw 'Program files remain after uninstall.' }
if (!(Test-Path -LiteralPath $received) -or !(Test-Path -LiteralPath $unexpected) -or (Get-FileHash -LiteralPath $cert).Hash -ne $certificateHash) { throw 'Uninstall changed user data.' }
Write-Output 'PASS uninstall preserves received data, device identity and unowned files'
Write-Output "Test artifacts: $run"
