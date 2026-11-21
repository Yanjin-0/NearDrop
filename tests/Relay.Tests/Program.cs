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
var exe = Path.Combine(root, "vendor", "syncthing-windows-amd64-v2.1.7", "syncthing.exe");
await using var a = new SyncthingEngine(exe, Path.Combine(run, "a"), true);
await using var b = new SyncthingEngine(exe, Path.Combine(run, "b"), true);
try
{
    await a.StartAsync(); await b.StartAsync();
    var id = a.DeviceId;
    Check(id != b.DeviceId, "獨立設定產生不同身分");
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
    var transfersA = new TransferService(a, Path.Combine(run, "app-a"));
    var transfersB = new TransferService(b, Path.Combine(run, "app-b"));
    var sample = Path.Combine(run, "測試資料"); Directory.CreateDirectory(Path.Combine(sample, "子目錄", "空目錄"));
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
    Console.WriteLine($"MEASURE snapshot+transfer {clock.Elapsed.TotalSeconds:F2}s, {send.TotalBytes} payload bytes");
    Check(!received.Saved, "傳輸完成不冒充安全保存");
    Check(Directory.Exists(Path.Combine(received.Path, "Files", "測試資料", "子目錄", "空目錄")), "保留繁體檔名、階層及空目錄");
    await transfersB.SaveReceivedAsync(received);
    Check(received.Saved && Directory.Exists(received.SavedPath), "停止引擎、逐檔驗證、隔離後獨立保存");
    Check((await b.ReadAsync("config/folders"))!.AsArray().Count == 0, "正式收件不再共享給朋友");
    var savedFile = Path.Combine(received.SavedPath!, "Files", "測試資料", "large.bin");
    Check(File.ReadAllText(savedFile + ":Zone.Identifier").Contains("ZoneId=3"), "保存檔案加入 Windows 網際網路安全標記");
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
        await WaitFor(async () => { await transfersB.RefreshAsync(); return mirror.State == "目前已同步 · 持續監控" && mirror.DoneBytes >= send.TotalBytes; },
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
    await transfersA.ToggleAsync(watch);
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
