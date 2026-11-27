using Relay.Core;
using Relay.Windows;
using System.Net;
using System.Text.Json.Nodes;

var root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
var run = Path.Combine(root, "test-results", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
var largeFile = args.Contains("--large-file");
var stress = largeFile || args.Contains("--stress");
var smallCount = stress ? 2000 : 250;
var largeMiB = largeFile ? 4097 : stress ? 1024 : 64;
var transferTimeout = largeFile ? 480 : 100;
Directory.CreateDirectory(run);
var results = new List<string>();
void Check(bool condition, string label)
{
    if (!condition) throw new Exception("FAIL: " + label);
    results.Add("PASS " + label); Console.WriteLine(results[^1]);
}
var driveRoot = Path.GetPathRoot(run)!;
Check(SafePaths.Within(run, driveRoot) && !SafePaths.Within(driveRoot, driveRoot),
    "磁碟根目錄能正確判斷下層路徑及本身");
using (var forwarding = new SingleInstance(Path.Combine(run, "forwarding")))
{
    try { await forwarding.ForwardAsync(Enumerable.Repeat("sample.txt", 4097).ToArray()); throw new Exception("Oversized selection accepted"); }
    catch (IOException e) { Check(e.Message.Contains("4,096", StringComparison.Ordinal), "超過帶入上限時回報原因與分批建議"); }
}
var exe = Path.Combine(root, "vendor", "syncthing-windows-amd64-v2.1.7", "syncthing.exe");
await using var a = new SyncthingEngine(exe, Path.Combine(run, "a"), true);
await using var b = new SyncthingEngine(exe, Path.Combine(run, "b"), true);
try
{
    await a.StartAsync(); await b.StartAsync();
    var id = a.DeviceId;
    Check(id != b.DeviceId, "獨立設定產生不同身分");
    Check(new Friend(id, "測試", false, "").Display.EndsWith("未連線", StringComparison.Ordinal) &&
          new Friend(id, "測試", true, "future-transport").ConnectionLabel == "連線方式未知" &&
          new Friend(id, "測試", true, "relay-server").ConnectionLabel == "中繼", "連線文案僅呈現引擎已知的狀態");
    Check(DeviceIdentity.Normalize(id) == id, "官方裝置 ID 檢查碼");
    Check((await a.ReadAsync("config/folders"))!.AsArray().Count == 0, "首次啟動沒有預設共享");
    var config = await a.GetAsync("config/gui");
    Check(config["address"]!.GetValue<string>().StartsWith("127.0.0.1:"), "REST 僅綁定回環位址");
    using var anonymous = new HttpClient(new HttpClientHandler { UseProxy = false });
    var response = await anonymous.GetAsync($"http://127.0.0.1:{a.GuiPort}/rest/system/status");
    Check(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, "無 API 金鑰無法控制引擎");
    var pairingA = new PairingService(a); var pairingB = new PairingService(b);
    try { await pairingA.AddAsync(b.DeviceId, "B", false); throw new Exception("Unverified identity accepted"); }
    catch (InvalidOperationException) { Check(true, "未核對身分不能配對"); }
    await pairingA.AddAsync(b.DeviceId, "測試朋友 B", true, [$"tcp://127.0.0.1:{b.ListenPort}"]);
    Check((await pairingB.ListAsync()).Count == 0, "單方加入不會授權另一端的朋友或共享");
    await pairingB.AddAsync(a.DeviceId, "測試朋友 A", true, [$"tcp://127.0.0.1:{a.ListenPort}"]);
    for (var i = 0; i < 60 && !(await pairingA.ListAsync()).Single().Connected; i++) await Task.Delay(1000);
    Check((await pairingA.ListAsync()).Single().Connected, "雙向核對後真實 TLS 連線");
    var d = (await a.ReadAsync("config/devices/" + b.DeviceId))!;
    Check(!d["introducer"]!.GetValue<bool>() && !d["autoAcceptFolders"]!.GetValue<bool>(), "關閉介紹人與自動接受資料夾");
    await a.WriteAsync("config/devices/" + b.DeviceId, new JsonObject { ["paused"] = true }, "PATCH");
    Check(!await pairingA.AddAsync(b.DeviceId, "重新命名的朋友 B", true), "相同裝置 ID 辨識為更新名稱");
    var renamed = await a.GetAsync("config/devices/" + b.DeviceId);
    Check(renamed["name"]?.GetValue<string>() == "重新命名的朋友 B" &&
          renamed["paused"]?.GetValue<bool>() == true && JsonNode.DeepEquals(d["addresses"], renamed["addresses"]),
        "更新名稱保留原本的位址及暫停設定");
    await a.WriteAsync("config/devices/" + b.DeviceId, new JsonObject { ["paused"] = false }, "PATCH");
    var transfersA = new TransferService(a, Path.Combine(run, "app-a"));
    try { transfersA.ValidateInbox(@"\\localhost\NearDropTest"); throw new Exception("Network share allowed as inbox"); }
    catch (IOException e) { Check(e.Message.Contains("本機 NTFS", StringComparison.Ordinal), "網路路徑顯示本機保存位置限制"); }
    var transfersB = new TransferService(b, Path.Combine(run, "app-b"));
    var preferenceProfile = Path.Combine(run, "preferences");
    var preferences = new TransferService(b, preferenceProfile);
    var oldInbox = preferences.State.Inbox;
    var proposedInbox = Path.Combine(run, "新保存位置");
    using (File.Open(Path.Combine(preferenceProfile, "state.json.new"), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
    {
        try { preferences.SetInbox(proposedInbox); throw new Exception("Locked settings unexpectedly written"); }
        catch (IOException) { Check(preferences.State.Inbox == oldInbox, "保存位置寫入失敗時保留原值"); }
        try { preferences.SetReceiveNotifications(false); throw new Exception("Locked notification preference unexpectedly written"); }
        catch (IOException) { Check(preferences.State.ReceiveNotifications, "通知偏好寫入失敗時保留原值"); }
    }
    preferences.SetInbox(proposedInbox);
    preferences.SetReceiveNotifications(false);
    var savedPreferences = new TransferService(b, preferenceProfile).State;
    Check(savedPreferences.Inbox == proposedInbox && !savedPreferences.ReceiveNotifications, "解除鎖定後可儲存並重新載入偏好");
    var sample = Path.Combine(run, "測試資料"); Directory.CreateDirectory(Path.Combine(sample, "子目錄", "空目錄"));
    var duplicateA = Path.Combine(Directory.CreateDirectory(Path.Combine(run, "同名甲")).FullName, "同名.txt");
    var duplicateB = Path.Combine(Directory.CreateDirectory(Path.Combine(run, "同名乙")).FullName, "同名.txt");
    await File.WriteAllTextAsync(duplicateA, "first");
    await File.WriteAllTextAsync(duplicateB, "second");
    try { await transfersA.SendAsync([duplicateA, duplicateB], (await pairingA.ListAsync()).Single()); throw new Exception("Conflicting draft accepted"); }
    catch (IOException e)
    {
        Check(e.Message.Contains("同名.txt", StringComparison.Ordinal) && transfersA.State.Jobs.Count == 0 &&
            !Directory.Exists(Path.Combine(run, "app-a", "Outbox")), "同名項目在建立工作及暫存前被攔截");
    }
    for (var i = 0; i < smallCount; i++) await File.WriteAllTextAsync(Path.Combine(sample, "子目錄", $"檔案 {i:D4}.txt"), $"測試內容 {i}\n");
    var large = Path.Combine(sample, "large.bin");
    await using (var stream = File.Create(large))
    {
        var block = new byte[1024 * 1024]; Random.Shared.NextBytes(block);
        for (var i = 0; i < largeMiB; i++) { Random.Shared.NextBytes(block); await stream.WriteAsync(block); }
    }
    var originalDigest = await DigestAsync(large);
    using (var cancelPreparation = new CancellationTokenSource())
    {
        var sawCopy = false;
        var published = false;
        var progress = new InlineProgress<SnapshotProgress>(p => { sawCopy = p.FileLength > 0; cancelPreparation.Cancel(); });
        try
        {
            await transfersA.SendAsync([large], (await pairingA.ListAsync()).Single(), cancelPreparation.Token, progress, () => published = true);
            throw new Exception("Cancelled snapshot was published");
        }
        catch (OperationCanceledException) when (cancelPreparation.IsCancellationRequested)
        {
            var cancelled = transfersA.State.Jobs.Single();
            Check(sawCopy && !published && cancelled.Detached && cancelled.OutboxRemoved && !Directory.Exists(cancelled.Path) && File.Exists(large),
                "從大檔複製進度取消後清理部分暫存，保留原始檔且未進入共享階段");
            Check((await a.ReadAsync("config/folders"))!.AsArray().Count == 0, "取消準備不建立任何引擎共享");
        }
    }
    var clock = System.Diagnostics.Stopwatch.StartNew();
    var send = await transfersA.SendAsync([sample], (await pairingA.ListAsync()).Single());
    Check(send.TotalBytes > (long)largeMiB * 1024 * 1024, $"{largeMiB} MiB 檔案及 {smallCount} 個小檔案建立不可變暫存");
    await engineScan(a, send.Id);
    async Task WaitFor(Func<Task<bool>> condition, string label, int seconds = 100)
    {
        for (var i = 0; i < seconds; i++) { if (await condition()) { Check(true, label); return; } await Task.Delay(1000); }
        throw new TimeoutException(label);
    }
    await WaitFor(async () => { await transfersB.RefreshAsync(); return transfersB.Offers.Count == 1; }, "只有已配對朋友的共享顯示待接受");
    Check((await b.ReadAsync("config/folders"))!.AsArray().Count == 0, "接收前不自動接受共享");
    await transfersB.AcceptAsync(transfersB.Offers.Single());
    var received = transfersB.State.Jobs.Single();
    await WaitFor(async () => { await transfersB.RefreshAsync(); return received.State == "已接收 · 等待獨立保存"; }, $"真實引擎完成 {largeMiB} MiB 及多檔傳輸", transferTimeout);
    await WaitFor(async () => { await transfersA.RefreshAsync(); return send.State == TransferJob.SenderSynced; }, "發送端確認對方同步完成");
    Console.WriteLine($"MEASURE snapshot+transfer {clock.Elapsed.TotalSeconds:F2}s, {send.TotalBytes} payload bytes");
    Check(!received.Saved, "傳輸完成不冒充安全保存");
    await transfersB.CancelAsync(received);
    await transfersB.RefreshAsync();
    var stoppedReceipt = new TransferService(b, Path.Combine(run, "app-b")).State.Jobs.Single(j => j.Id == received.Id);
    Check(stoppedReceipt.Detached && stoppedReceipt.IsAwaitingFirstSave && stoppedReceipt.CanSave &&
          !stoppedReceipt.CanRetry && !stoppedReceipt.CanStop &&
          (await b.ReadAsync("config/folders"))!.AsArray().Count == 0,
        "已完整接收的工作停止共享後仍可保存，重載時也保留入口");
    Check(Directory.Exists(Path.Combine(received.Path, "Files", "測試資料", "子目錄", "空目錄")), "保留繁體檔名、階層及空目錄");
    await transfersB.SaveReceivedAsync(received);
    Check(received.Saved && Directory.Exists(received.SavedPath), "停止引擎、逐檔驗證、隔離後獨立保存");
    await WaitFor(async () => (await a.GetAsync("db/completion?folder=" + send.Id + "&device=" + b.DeviceId))["remoteState"]?.GetValue<string>() == "notSharing",
        "收件保存後對方停止共享");
    await transfersA.RefreshAsync();
    Check(send.State == TransferJob.SenderSynced, "對方保存後發送紀錄仍保留已確認的同步結果");
    Check((await b.ReadAsync("config/folders"))!.AsArray().Count == 0, "正式收件不再共享給朋友");
    var savedFile = Path.Combine(received.SavedPath!, "Files", "測試資料", "large.bin");
    Check(File.ReadAllText(savedFile + ":Zone.Identifier").Contains("ZoneId=3"), "保存檔案加入 Windows 網際網路安全標記");
    var recoverySource = Path.Combine(run, "保存恢復.txt");
    await File.WriteAllTextAsync(recoverySource, "保存恢復測試");
    var recoverySend = await transfersA.SendAsync([recoverySource], (await pairingA.ListAsync()).Single());
    await engineScan(a, recoverySend.Id);
    await WaitFor(async () => { await transfersB.RefreshAsync(); return transfersB.Offers.Any(o => o.Id == recoverySend.Id); }, "保存恢復測試收到邀請");
    await transfersB.AcceptAsync(transfersB.Offers.Single(o => o.Id == recoverySend.Id));
    var recovery = transfersB.State.Jobs.Single(j => j.Id == recoverySend.Id);
    await WaitFor(async () => { await transfersB.RefreshAsync(); return recovery.CanSave; }, "保存恢復測試完成收件");
    recovery.SavedPath = Path.Combine(transfersB.State.Inbox, "Received-" + recovery.Id[8..]);
    recovery.State = "正在隔離與驗證";
    transfersB.SaveSettings();
    // Simulate a crash after removing the share and before recording detachment.
    await b.WriteAsync("config/folders/" + recovery.Id, null, "DELETE");
    var recoveredReceiver = new TransferService(b, Path.Combine(run, "app-b"));
    var recovered = recoveredReceiver.State.Jobs.Single(j => j.Id == recovery.Id);
    await recoveredReceiver.RefreshAsync();
    Check(recovered.State == TransferJob.SaveInterrupted && recovered.CanSave && !recovered.CanRetry && !recovered.CanStop,
        "重新啟動後辨識保存中斷並只提供保存重試");
    Directory.CreateDirectory(recovered.SavedPath!);
    try { await recoveredReceiver.SaveReceivedAsync(recovered); throw new Exception("Unrelated target accepted"); }
    catch (EngineRestoreException e) { throw new Exception("The engine did not recover after the simulated save failure.", e); }
    catch (IOException)
    {
        Check(recovered.State == TransferJob.SaveFailed && recovered.Error != null && recovered.Detached && Directory.Exists(recovered.Path) && b.Running,
            "保存失敗時保留本機收件及原因，並恢復引擎");
    }
    await recoveredReceiver.RefreshAsync();
    var storedFailure = new TransferService(b, Path.Combine(run, "app-b")).State.Jobs.Single(j => j.Id == recovery.Id);
    Check(storedFailure.State == TransferJob.SaveFailed && storedFailure.CanSave && storedFailure.Error == recovered.Error,
        "保存失敗原因在重新整理及重新載入後仍保留");
    Directory.Delete(recovered.SavedPath!);
    await recoveredReceiver.SaveReceivedAsync(recovered);
    var recoveredFile = Path.Combine(recovered.SavedPath!, "Files", Path.GetFileName(recoverySource));
    Check(recovered.Saved && recovered.Error == null && await DigestAsync(recoveredFile) == await DigestAsync(recoverySource),
        "解除目的資料夾衝突後可重試獨立保存");
    var badId = "nd-once-" + Guid.NewGuid().ToString("N");
    var rejected = new TransferJob
    {
        Id = badId, Kind = "receive", FriendId = a.DeviceId, Detached = true, State = TransferJob.SaveInterrupted,
        Path = Path.Combine(run, "app-b", "Incoming", badId),
        SavedPath = Path.Combine(recoveredReceiver.State.Inbox, "Received-" + badId[8..])
    };
    await Snapshots.CreateAsync([recoverySource], rejected.Path, badId);
    await File.WriteAllTextAsync(Path.Combine(rejected.Path, "Files", Path.GetFileName(recoverySource)), "modified");
    recoveredReceiver.State.Jobs.Add(rejected);
    recoveredReceiver.SaveSettings();
    try { await recoveredReceiver.SaveReceivedAsync(rejected); throw new Exception("Changed receipt was saved"); }
    catch (IOException e)
    {
        Check(rejected.State == TransferJob.SaveRejected && !rejected.CanSave && !rejected.CanRetry && e.Message.Contains("新的傳送邀請", StringComparison.Ordinal),
            "內容驗證失敗提示對方重送並移除無效的重試動作");
    }
    await recoveredReceiver.RefreshAsync();
    var savedRejection = new TransferService(b, Path.Combine(run, "app-b")).State.Jobs.Single(job => job.Id == badId);
    Check(savedRejection.State == TransferJob.SaveRejected && !savedRejection.CanSave && Directory.Exists(rejected.Path) &&
          !Directory.Exists(rejected.SavedPath), "驗證拒絕可重新載入，未將原副本移到保存位置");
    transfersB = recoveredReceiver;
    var declined = await transfersA.SendAsync([Path.Combine(sample, "子目錄", "檔案 0000.txt")], (await pairingA.ListAsync()).Single());
    await WaitFor(async () => { await transfersB.RefreshAsync(); return transfersB.Offers.Any(o => o.Id == declined.Id); }, "第二筆傳送顯示待接受邀請");
    var rejectedOffer = transfersB.Offers.Single(o => o.Id == declined.Id);
    await transfersB.DeclineAsync(rejectedOffer);
    var restartedReceiver = new TransferService(b, Path.Combine(run, "app-b"));
    await restartedReceiver.RefreshAsync();
    Check(restartedReceiver.Offers.All(o => o.Id != declined.Id) && (await b.ReadAsync("config/folders"))!.AsArray().Count == 0, "拒絕後不加入共享，重新載入也不再顯示");
    try { await restartedReceiver.AcceptAsync(rejectedOffer); throw new Exception("Declined offer accepted"); }
    catch (InvalidOperationException) { Check(true, "拒絕後的舊邀請不能被接受"); }
    try { await transfersA.ClearOutboxAsync(declined); throw new Exception("Active snapshot deleted"); }
    catch (InvalidOperationException) { Check(Directory.Exists(declined.Path), "仍在共享的暫存不可清理"); }
    declined.Detached = true; // Simulate stale local metadata. The live engine still has the share.
    try { await transfersA.ClearOutboxAsync(declined); throw new Exception("Engine share ignored"); }
    catch (IOException) { Check(Directory.Exists(declined.Path) && !declined.CleanupStarted, "即使本機紀錄標示已停止，仍須確認實際共享已撤銷"); }
    await transfersA.CancelAsync(declined);
    var ownedSnapshot = transfersA.GetOutboxPath(declined);
    var lockedFile = Path.Combine(ownedSnapshot, "Files", "檔案 0000.txt");
    using (File.OpenRead(lockedFile))
    {
        try { await transfersA.ClearOutboxAsync(declined); throw new Exception("Locked snapshot deleted"); }
        catch (IOException) { Check(declined.CleanupStarted && !declined.OutboxRemoved && File.Exists(lockedFile), "鎖定檔案保留，部分清理可重試"); }
    }
    var resumedSender = new TransferService(a, Path.Combine(run, "app-a"));
    var pendingCleanup = resumedSender.State.Jobs.Single(j => j.Id == declined.Id);
    try { await resumedSender.RetryAsync(pendingCleanup); throw new Exception("Partial snapshot shared again"); }
    catch (InvalidOperationException) { Check(pendingCleanup.CleanupStarted, "重新載入後仍禁止重送已開始清理的暫存"); }
    var originalDeclinedPath = declined.Path;
    declined.Path = sample;
    try { await transfersA.ClearOutboxAsync(declined); throw new Exception("Unexpected outbox path accepted"); }
    catch (IOException) { Check(Directory.Exists(sample), "阻擋工作紀錄指向原始資料夾"); }
    declined.Path = originalDeclinedPath;
    await transfersA.ClearOutboxAsync(declined);
    Check(declined.OutboxRemoved && !Directory.Exists(ownedSnapshot) && File.Exists(Path.Combine(sample, "子目錄", "檔案 0000.txt")) &&
          Directory.Exists(send.Path) && File.Exists(savedFile), "清理只刪除指定暫存，保留來源、其他工作及已保存收件");
    var afterCleanup = new TransferService(a, Path.Combine(run, "app-a"));
    Check(afterCleanup.State.Jobs.Single(j => j.Id == declined.Id).OutboxRemoved, "清理完成後保留工作紀錄");
    File.Delete(large); // Explicitly remove the original source; receiver must remain intact.
    File.Delete(Path.Combine(send.Path, "Files", "測試資料", "large.bin"));
    await engineScan(a, send.Id);
    await transfersA.CancelAsync(send);
    Check(new FileInfo(savedFile).Length == (long)largeMiB * 1024 * 1024, "刪除原始來源及共享暫存、掃描刪除後不影響正式收件");
    Check(originalDigest == await DigestAsync(savedFile), "原始檔與獨立保存檔的 SHA-256 相同");
    Check(send.HasConfirmedPeerSync && !send.CanRetry && send.Display.Contains("先前已確認對方同步", StringComparison.Ordinal),
        "停止共享保留同步結果，重送需要新邀請");
    await transfersA.ClearOutboxAsync(send);
    var completedHistory = new TransferService(a, Path.Combine(run, "app-a")).State.Jobs.Single(j => j.Id == send.Id);
    Check(completedHistory.OutboxRemoved && completedHistory.HasConfirmedPeerSync &&
          TransferHistory.StatusOf(completedHistory) == HistoryStatus.Stopped &&
          TransferHistory.Search([completedHistory], "", HistoryStatus.Delivered, HistoryDirection.Outgoing).Length == 1 &&
          originalDigest == await DigestAsync(savedFile), "清理後可重新載入歷史同步結果，且保留正式收件");
    foreach (var bad in new[] { "../escape", "C:\\escape", "a:stream", "CON.txt", "x./a", "a/../../b" })
    {
        try { SafePaths.Relative(run, bad); throw new Exception("Unsafe accepted " + bad); }
        catch (IOException) { }
    }
    Check(true, "拒絕路徑穿越、ADS、裝置檔名及絕對路徑");
    var corrupt = Path.Combine(run, "corrupt");
    await Snapshots.CreateAsync([Path.Combine(sample, "子目錄")], corrupt, "test");
    await File.WriteAllTextAsync(Path.Combine(corrupt, "Files", "子目錄", "檔案 0000.txt"), "tampered");
    try { await Snapshots.VerifyAsync(corrupt, "test"); throw new Exception("Tamper accepted"); }
    catch (IOException) { Check(true, "清單雜湊不符時拒絕保存"); }
    var watchSource = Path.Combine(run, "自動資料夾"); Directory.CreateDirectory(watchSource);
    await File.WriteAllTextAsync(Path.Combine(watchSource, "auto.txt"), "first");
    if (stress)
    {
        // Same payload as the one-shot run, available before enabling this monitor.
        var benchmarkSource = Path.Combine(received.SavedPath!, "Files", "測試資料");
        foreach (var item in SafePaths.Walk(benchmarkSource))
        {
            var target = Path.Combine(watchSource, Path.GetRelativePath(benchmarkSource, item));
            if (Directory.Exists(item)) Directory.CreateDirectory(target); else File.Copy(item, target);
        }
    }
    var initialWatchClock = System.Diagnostics.Stopwatch.StartNew();
    var watch = await transfersA.WatchAsync(watchSource, (await pairingA.ListAsync()).Single());
    await engineScan(a, watch.Id);
    await WaitFor(async () => { await transfersB.RefreshAsync(); return transfersB.Offers.Any(o => o.Id == watch.Id); }, "固定資料夾送出明確邀請");
    await transfersB.AcceptAsync(transfersB.Offers.Single(o => o.Id == watch.Id));
    var mirror = transfersB.State.Jobs.Single(j => j.Id == watch.Id);
    var reserve = (await b.GetAsync("config/folders/" + watch.Id))["minDiskFree"]!;
    Check(reserve["value"]!.GetValue<int>() == ReceiveDiskPolicy.ReserveGB && reserve["unit"]!.GetValue<string>() == "GB", "既有引擎依設定保護收件磁碟剩餘空間");
    Check(!ReceiveDiskPolicy.HasRoom(1_999_999_999L) && ReceiveDiskPolicy.HasRoom(2_000_000_000L) &&
          !ReceiveDiskPolicy.HasRoom(2_000_000_001L, 2L) && ReceiveDiskPolicy.HasRoom(2_000_000_001L, 1L), "收件與跨磁碟保存空間門檻正確");
    await WaitFor(() => Task.FromResult(File.Exists(Path.Combine(mirror.Path, "auto.txt"))), "固定資料夾初始同步");
    if (stress)
    {
        await WaitFor(async () => { await transfersB.RefreshAsync(); return mirror.State == "已同步至目前收到的版本 · 持續接收" && mirror.DoneBytes >= send.TotalBytes; },
            $"固定資料夾實傳與單次相同的 {largeMiB} MiB、{smallCount} 小檔負載", transferTimeout);
        Check(originalDigest == await DigestAsync(Path.Combine(mirror.Path, "large.bin")), "原始檔與固定同步檔的 SHA-256 相同");
        Console.WriteLine($"MEASURE native watch initial sync {initialWatchClock.Elapsed.TotalSeconds:F2}s, {mirror.DoneBytes} payload bytes");
    }
    var watchClock = System.Diagnostics.Stopwatch.StartNew();
    await File.WriteAllTextAsync(Path.Combine(watchSource, "auto.txt"), "second version");
    await WaitFor(async () => File.Exists(Path.Combine(mirror.Path, "auto.txt")) && await File.ReadAllTextAsync(Path.Combine(mirror.Path, "auto.txt")) == "second version", "原生檔案監控自動傳送修改（未手動掃描）");
    Console.WriteLine($"MEASURE native watch change {watchClock.Elapsed.TotalSeconds:F2}s");
    await transfersA.ToggleAsync(watch);
    Check((await a.GetAsync("config/folders/" + watch.Id))["paused"]!.GetValue<bool>(), "固定資料夾可暫停");
    await WaitFor(async () => { await transfersB.RefreshAsync(); return mirror.State == "對方已暫停同步"; }, "接收端可辨識對方暫停同步");
    await a.WriteAsync("config/folders/" + watch.Id, new JsonObject { ["fsWatcherDelayS"] = 14 }, "PATCH");
    await transfersA.RetryAsync(watch);
    var restoredWatch = await a.GetAsync("config/folders/" + watch.Id);
    Check(restoredWatch["fsWatcherDelayS"]?.GetValue<int>() == 14 && restoredWatch["paused"]?.GetValue<bool>() == false,
        "繼續同步及重新掃描沿用現有共享設定");
    await a.WriteAsync("config/folders/" + watch.Id, new JsonObject { ["fsWatcherDelayS"] = 10 }, "PATCH");
    await transfersB.ToggleAsync(mirror);
    await WaitFor(async () => { await transfersA.RefreshAsync(); return watch.State == "對方已暫停同步"; }, "發送端可辨識對方暫停同步");
    await transfersB.ToggleAsync(mirror);
    var zeroByteSource = Directory.CreateDirectory(Path.Combine(run, "零容量同步")).FullName;
    Directory.CreateDirectory(Path.Combine(zeroByteSource, "空目錄"));
    await File.WriteAllBytesAsync(Path.Combine(zeroByteSource, "空檔案.txt"), []);
    var zeroByteWatch = await transfersA.WatchAsync(zeroByteSource, (await pairingA.ListAsync()).Single());
    await WaitFor(async () => { await transfersB.RefreshAsync(); return transfersB.Offers.Any(o => o.Id == zeroByteWatch.Id); }, "零容量同步送出邀請");
    await transfersB.AcceptAsync(transfersB.Offers.Single(o => o.Id == zeroByteWatch.Id));
    var zeroByteMirror = transfersB.State.Jobs.Single(j => j.Id == zeroByteWatch.Id);
    await WaitFor(async () =>
    {
        await transfersA.RefreshAsync();
        await transfersB.RefreshAsync();
        return zeroByteWatch.State == "對方已同步 · 持續傳送" && zeroByteMirror.State == "已同步至目前收到的版本 · 持續接收";
    }, "零位元組檔案和空目錄也可確認同步完成");
    Check(File.Exists(Path.Combine(zeroByteMirror.Path, "空檔案.txt")) && Directory.Exists(Path.Combine(zeroByteMirror.Path, "空目錄")),
        "零容量同步保留檔案及目錄");
    await File.WriteAllTextAsync(Path.Combine(zeroByteMirror.Path, "僅本機.txt"), "local");
    await b.WriteAsync("db/scan?folder=" + zeroByteMirror.Id, null);
    await WaitFor(async () => { await transfersB.RefreshAsync(); return zeroByteMirror.State == "本機有變更 · 不會傳回對方"; },
        "接收端本機修改有獨立狀態");
    Check(!File.Exists(Path.Combine(zeroByteSource, "僅本機.txt")), "接收端本機修改不會傳回來源");
    await transfersA.CancelAsync(zeroByteWatch);
    await WaitFor(async () => { await transfersB.RefreshAsync(); return zeroByteMirror.State == "對方目前未啟用此共享"; },
        "接收端可辨識對方停止共享");
    Check(File.Exists(Path.Combine(zeroByteMirror.Path, "空檔案.txt")), "對方停止共享後，本機已同步的檔案仍在");
    var replacementWatch = await transfersA.WatchAsync(zeroByteSource, (await pairingA.ListAsync()).Single());
    try { await transfersA.RetryAsync(zeroByteWatch); throw new Exception("Overlapping watch was restored"); }
    catch (IOException e)
    {
        Check(e.Message.Contains("重疊", StringComparison.Ordinal) && zeroByteWatch.Detached,
            "重新啟用時拒絕已被其他工作使用的同步來源");
    }
    await transfersA.CancelAsync(replacementWatch);
    // A missing source can be tested without racing the Windows watcher for a directory rename.
    var unavailableWatchSource = Path.Combine(run, "不存在的同步來源");
    zeroByteWatch.Path = unavailableWatchSource;
    try { await transfersA.RetryAsync(zeroByteWatch); throw new Exception("Missing watch source was recreated"); }
    catch (IOException e)
    {
        Check(e.Message.Contains("找不到原本", StringComparison.Ordinal) && !Directory.Exists(unavailableWatchSource) && zeroByteWatch.Detached,
            "重新啟用時拒絕已搬移的同步來源");
    }
    zeroByteWatch.Path = zeroByteSource;
    await transfersA.RetryAsync(zeroByteWatch);
    Check(!zeroByteWatch.Detached && zeroByteWatch.Error == null, "來源恢復後可重新啟用共享");
    var devicePermissions = (await a.GetAsync("config/folders/" + watch.Id))["devices"]!.AsArray();
    Check(devicePermissions.Count == 2 && devicePermissions.Any(p => p!["deviceID"]!.GetValue<string>() == b.DeviceId), "每個共享僅本機與指定朋友");
    Check((await a.GetAsync("config/folders/" + watch.Id))["type"]!.GetValue<string>() == "sendonly", "固定資料夾不授予朋友反向寫入來源權限");
    await using var c = new SyncthingEngine(exe, Path.Combine(run, "c"), true);
    await c.StartAsync();
    var pairingC = new PairingService(c);
    await pairingC.AddAsync(a.DeviceId, "A", true, [$"tcp://127.0.0.1:{a.ListenPort}"]);
    await Task.Delay(1000);
    Check((await pairingA.ListAsync()).All(f => f.DeviceId != c.DeviceId), "陌生節點嘗試連線不會自動取得授權");
    await pairingA.AddAsync(c.DeviceId, "朋友 C", true, [$"tcp://127.0.0.1:{c.ListenPort}"]);
    await WaitFor(async () => (await pairingA.ListAsync()).Any(f => f.DeviceId == c.DeviceId && f.Connected), "第二位朋友建立真實連線");
    var transfersC = new TransferService(c, Path.Combine(run, "app-c"));
    await transfersC.RefreshAsync();
    Check(transfersC.Offers.Count == 0, "另一位已配對朋友看不到 B 的共享邀請");
    var unauthorized = await c.GetAsync("config/defaults/folder");
    var forbiddenPath = Path.Combine(run, "c-forbidden"); Directory.CreateDirectory(forbiddenPath);
    unauthorized["id"] = watch.Id; unauthorized["path"] = forbiddenPath; unauthorized["type"] = "receiveonly";
    unauthorized["devices"] = new JsonArray(new JsonObject { ["deviceID"] = c.DeviceId }, new JsonObject { ["deviceID"] = a.DeviceId });
    await c.WriteAsync("config/folders", unauthorized);
    await Task.Delay(2500);
    Check(!File.Exists(Path.Combine(forbiddenPath, "auto.txt")), "已配對朋友即使知道另一共享 ID 也無法取得其檔案");
    await pairingA.RemoveAsync(c.DeviceId);
    await b.WriteAsync("config/folders/" + watch.Id, new JsonObject { ["minDiskFree"] = new JsonObject { ["value"] = 1, ["unit"] = "%" } }, "PATCH");
    await b.DisposeAsync();
    await Task.Delay(1000);
    await File.WriteAllTextAsync(Path.Combine(watchSource, "offline.txt"), "離線後續傳");
    await a.WriteAsync("db/scan?folder=" + watch.Id, null);
    await b.StartAsync();
    var migrated = (await b.GetAsync("config/folders/" + watch.Id))["minDiskFree"]!;
    Check(migrated["value"]!.GetValue<int>() == ReceiveDiskPolicy.ReserveGB && migrated["unit"]!.GetValue<string>() == "GB", "舊版收件共享隨引擎重新啟動套用磁碟保護");
    await pairingA.AddAsync(b.DeviceId, "測試朋友 B", true, [$"tcp://127.0.0.1:{b.ListenPort}"]);
    await WaitFor(() => Task.FromResult(File.Exists(Path.Combine(mirror.Path, "offline.txt"))), "朋友離線重啟後自動恢復");
    var reloaded = new TransferService(b, Path.Combine(run, "app-b"));
    Check(reloaded.State.Jobs.Single(j => j.Id == received.Id).Saved, "保存日誌可在重啟後恢復");
    await pairingA.RemoveAsync(b.DeviceId);
    Check((await pairingA.ListAsync()).Count == 0, "解除朋友配對");
    Check(!(await a.GetAsync("config/folders/" + watch.Id))["devices"]!.AsArray().Any(p => p!["deviceID"]!.GetValue<string>() == b.DeviceId), "解除配對撤銷既有資料夾共享");
    await a.DisposeAsync();
    Check(!a.Running, "退出停止引擎");
    await a.StartAsync();
    Check(a.DeviceId == id, "重新啟動裝置 ID 不變");
    await using var duplicate = new SyncthingEngine(exe, Path.Combine(run, "a"), true);
    try { await duplicate.StartAsync(); throw new Exception("Duplicate allowed"); }
    catch (IOException) { Check(true, "同一設定不能啟動第二個引擎"); }
    File.WriteAllLines(Path.Combine(run, "results.txt"), results);
    Console.WriteLine("Results: " + run);
}
catch (Exception e)
{
    Console.Error.WriteLine(e);
    File.WriteAllLines(Path.Combine(run, "results.txt"), results.Append("FAIL " + e));
    Environment.ExitCode = 1;
}
static Task engineScan(SyncthingEngine engine, string folder) => engine.WriteAsync("db/scan?folder=" + folder, null);
static async Task<string> DigestAsync(string path)
{
    await using var stream = File.OpenRead(path);
    return Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream));
}
sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
