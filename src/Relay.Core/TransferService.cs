using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Relay.Core;

public sealed partial class TransferService
{
    private readonly ITransferEngine engine;
    private readonly string profile;
    private readonly StateStore store;
    private readonly SemaphoreSlim gate = new(1);
    public AppState State { get; }
    public IReadOnlyList<IncomingOffer> Offers { get; private set; } = [];
    public TransferService(ITransferEngine engine, string profile)
    {
        this.engine = engine; this.profile = Path.GetFullPath(profile);
        Directory.CreateDirectory(profile);
        store = new StateStore(Path.Combine(profile, "state.json"));
        State = store.Load();
        if (State.Inbox == "") State.Inbox = Path.Combine(profile, "Received");
        Directory.CreateDirectory(State.Inbox);
    }
    public void SaveSettings() => store.Save(State);
    private async Task Guard(Func<Task> operation)
    {
        await gate.WaitAsync();
        try { await operation(); }
        finally { gate.Release(); }
    }
    public async Task<TransferJob> SendAsync(string[] sources, Friend friend, CancellationToken ct = default,
        IProgress<SnapshotProgress>? progress = null, Action? publishing = null)
    {
        TransferJob? result = null;
        await Guard(async () =>
        {
            ct.ThrowIfCancellationRequested();
            await RequireFriend(friend.DeviceId);
            ct.ThrowIfCancellationRequested();
            if (sources.Length == 0) throw new ArgumentException("請先選取檔案。");
            foreach (var source in sources)
            {
                SafePaths.NoLinks(source);
                // Never send engine credentials, queues or their ancestors.
                if (SafePaths.Overlap(source, Path.Combine(profile, "engine")) || SafePaths.Overlap(source, Path.Combine(profile, "Outbox")) || SafePaths.Overlap(source, Path.Combine(profile, "Incoming")))
                    throw new IOException("請選取一般檔案，不能傳送應用程式設定或傳輸暫存。");
            }
            var job = new TransferJob { Id = "nd-once-" + Guid.NewGuid().ToString("N"), FriendId = friend.DeviceId, FriendName = friend.Name,
                Label = sources.Length == 1 ? Path.GetFileName(Path.TrimEndingDirectorySeparator(sources[0])) : $"{sources.Length} 個項目", Kind = "send" };
            job.Path = Path.Combine(profile, "Outbox", job.Id);
            State.Jobs.Add(job); store.Save(State); result = job;
            var publicationStarted = false;
            try
            {
                job.TotalBytes = await Task.Run(() => Snapshots.CreateAsync(sources, job.Path, job.Id, ct, progress), ct);
                ct.ThrowIfCancellationRequested();
                publishing?.Invoke();
                publicationStarted = true;
                job.State = "暫存完成，等待連線"; store.Save(State);
                await ConfigureFolder(job); job.State = "已排入傳送"; store.Save(State);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested && !publicationStarted)
            {
                job.Detached = job.CleanupStarted = true;
                job.State = "已取消準備 · 未開始共享";
                store.Save(State);
                try { await Task.Run(() => DeleteOutboxTree(GetOutboxPath(job))); job.OutboxRemoved = true; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                { job.Error = "部分暫存尚未清除，可在傳輸紀錄重試清理。"; }
                store.Save(State);
                throw;
            }
            catch (Exception e) { job.State = "準備／傳送失敗"; job.Error = e.Message; store.Save(State); throw; }
        });
        return result!;
    }
    public async Task<TransferJob> WatchAsync(string source, Friend friend)
    {
        TransferJob? result = null;
        await Guard(async () =>
        {
            await RequireFriend(friend.DeviceId);
            source = Path.GetFullPath(source); SafePaths.NoLinks(source);
            if (!Directory.Exists(source)) throw new IOException("資料夾不存在。");
            foreach (var sensitive in new[] { profile, State.Inbox, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppContext.BaseDirectory })
                if (sensitive.Length > 0 && SafePaths.Overlap(source, sensitive)) throw new IOException("不能監控系統、設定、收件或其上層資料夾，請選用專用的傳送資料夾。");
            if (State.Jobs.Any(j => !j.Detached && SafePaths.Overlap(j.Path, source))) throw new IOException("不能建立重疊的共享資料夾。");
            // Initial traversal is validation only. Syncthing's native watcher handles later changes.
            await Task.Run(() => { foreach (var _ in SafePaths.Walk(source)) { } });
            var job = new TransferJob { Id = "nd-live-" + Guid.NewGuid().ToString("N"), FriendId = friend.DeviceId,
                FriendName = friend.Name, Label = Path.GetFileName(source), Path = source, Kind = "watch-send", State = "已排入持續同步" };
            State.Jobs.Add(job); store.Save(State); result = job;
            await ConfigureFolder(job); store.Save(State);
        });
        return result!;
    }
    private async Task RequireFriend(string id)
    {
        var list = await new PairingService(engine).ListAsync();
        if (list.All(f => f.DeviceId != id)) throw new InvalidOperationException("朋友已解除配對；不能建立或恢復共享。");
    }
    private async Task ConfigureFolder(TransferJob job)
    {
        await RequireFriend(job.FriendId);
        SafePaths.NoLinks(job.Path);
        var folder = await engine.GetAsync("config/defaults/folder");
        folder["id"] = job.Id; folder["label"] = job.Label; folder["path"] = job.Path;
        folder["type"] = job.Kind.EndsWith("send") ? "sendonly" : "receiveonly";
        folder["paused"] = job.Paused;
        folder["fsWatcherEnabled"] = true; folder["fsWatcherDelayS"] = 10; folder["rescanIntervalS"] = 3600;
        folder["ignorePerms"] = true;
        folder["junctionsAsDirs"] = false;
        if (job.Kind.EndsWith("receive", StringComparison.Ordinal))
            folder["minDiskFree"] = ReceiveDiskPolicy.EngineSetting();
        folder["devices"] = new JsonArray(new JsonObject { ["deviceID"] = engine.DeviceId }, new JsonObject { ["deviceID"] = job.FriendId });
        if (job.Kind.StartsWith("watch-receive"))
            folder["versioning"] = new JsonObject { ["type"] = "simple", ["params"] = new JsonObject { ["keep"] = "5", ["cleanoutDays"] = "0" }, ["cleanupIntervalS"] = 3600 };
        await engine.WriteAsync("config/folders", folder);
    }
    public Task RefreshAsync() => Guard(async () =>
    {
        var friends = await new PairingService(engine).ListAsync();
        var pending = await engine.GetAsync("cluster/pending/folders");
        var offers = new List<IncomingOffer>();
        foreach (var (id, folder) in pending)
        {
            if (!FolderId().IsMatch(id) || State.Jobs.Any(j => j.Id == id)) continue;
            var by = folder?["offeredBy"]?.AsObject();
            if (by == null || by.Count != 1) continue; // Ambiguous offers never get merged.
            foreach (var (device, info) in by)
            {
                var friend = friends.FirstOrDefault(f => f.DeviceId == device);
                if (friend == null || info?["receiveEncrypted"]?.GetValue<bool>() == true || info?["remoteEncrypted"]?.GetValue<bool>() == true) continue;
                if (State.DeclinedOffers.Contains(OfferKey(id, device))) continue;
                var label = info?["label"]?.GetValue<string>() ?? "檔案";
                offers.Add(new IncomingOffer(id, device, friend.Name, label.Length > 120 ? label[..120] : label, id.StartsWith("nd-live-")));
            }
        }
        Offers = offers;
        var configured = (await engine.ReadAsync("config/folders"))!.AsArray();
        foreach (var job in State.Jobs.Where(j => !j.Saved && !j.Detached))
        {
            try
            {
                var config = configured.FirstOrDefault(f => f!["id"]!.GetValue<string>() == job.Id);
                if (config == null) { if (job.State == "準備中") job.State = "準備中斷，請重新選取來源傳送"; continue; }
                if (!config["devices"]!.AsArray().Any(d => d!["deviceID"]!.GetValue<string>() == job.FriendId))
                { job.State = "朋友權限已撤銷"; continue; }
                if (job.Paused) { job.State = "已暫停"; continue; }
                if (job.Kind.EndsWith("receive", StringComparison.Ordinal))
                {
                    try { ReceiveDiskPolicy.RequireRoom(job.Path); }
                    catch (IOException e) { job.State = "磁碟空間不足"; job.Error = e.Message; continue; }
                }
                var friend = friends.FirstOrDefault(f => f.DeviceId == job.FriendId);
                var status = await engine.GetAsync("db/status?folder=" + job.Id);
                if ((status["errors"]?.GetValue<int>() ?? 0) > 0 || (status["pullErrors"]?.GetValue<int>() ?? 0) > 0)
                { job.State = "部分檔案無法處理"; job.Error = "請確認檔案權限與可用磁碟空間後重試"; continue; }
                var outgoing = job.Kind.EndsWith("send");
                var progress = outgoing ? await engine.GetAsync("db/completion?folder=" + job.Id + "&device=" + job.FriendId) : status;
                var total = progress["globalBytes"]?.GetValue<long>() ?? 0;
                var need = progress["needBytes"]?.GetValue<long>() ?? 0;
                if (total > 0) job.TotalBytes = total;
                var done = Math.Clamp(total - need, 0, total);
                job.RecentBytesPerSecond = job.LastProgressAt == default ? 0 : Math.Max(0, done - job.DoneBytes) / Math.Max(0.1, (DateTime.UtcNow - job.LastProgressAt).TotalSeconds);
                job.LastProgressAt = DateTime.UtcNow; job.DoneBytes = done;
                var complete = total > 0 && need == 0 && (progress["needItems"]?.GetValue<long>() ??
                    (status["needFiles"]?.GetValue<long>() ?? 0) + (status["needDirectories"]?.GetValue<long>() ?? 0) + (status["needDeletes"]?.GetValue<long>() ?? 0)) == 0;
                if (outgoing) complete &= progress["remoteState"]?.GetValue<string>() == "valid" && friend?.Connected == true;
                else complete &= status["state"]?.GetValue<string>() == "idle";
                if (!outgoing && job.Kind == "receive") complete &= File.Exists(Path.Combine(job.Path, Snapshots.ManifestName));
                job.Error = null;
                job.State = complete ? job.Kind switch { "send" => TransferJob.SenderSynced, "receive" => TransferJob.AwaitingSave, _ => "目前已同步 · 持續監控" }
                    : friend?.Connected != true ? "朋友離線 · 上線後自動續傳" : outgoing && progress["remoteState"]?.GetValue<string>() == "notSharing" ? "等待朋友接受／對方已停止共享" : $"傳輸中 · {friend.Connection}";
            }
            catch (Exception e) when (e is HttpRequestException or IOException) { job.Error = e.Message; job.State = "暫時無法取得狀態"; }
        }
        store.Save(State);
    });
    public Task AcceptAsync(IncomingOffer offer) => Guard(async () =>
    {
        await RequireFriend(offer.FriendId);
        if (!FolderId().IsMatch(offer.Id) || State.Jobs.Any(j => j.Id == offer.Id) || State.DeclinedOffers.Contains(OfferKey(offer.Id, offer.FriendId)))
            throw new InvalidOperationException("此傳送已處理。");
        // Validate the current offer again, instead of trusting a stale UI object.
        var pending = await engine.GetAsync("cluster/pending/folders");
        var sources = pending[offer.Id]?["offeredBy"]?.AsObject();
        var info = sources?[offer.FriendId];
        if (sources?.Count != 1 || info == null || info["receiveEncrypted"]?.GetValue<bool>() == true || info["remoteEncrypted"]?.GetValue<bool>() == true)
            throw new IOException("來源已變更或撤銷，請更新邀請後重試。");
        var job = new TransferJob { Id = offer.Id, FriendId = offer.FriendId, FriendName = offer.FriendName, Label = offer.Label,
            Kind = offer.Id.StartsWith("nd-live-", StringComparison.Ordinal) ? "watch-receive" : "receive", Path = Path.Combine(profile, "Incoming", offer.Id), State = "正在接收" };
        ReceiveDiskPolicy.RequireRoom(job.Path);
        Directory.CreateDirectory(job.Path); SafePaths.NoLinks(job.Path);
        State.Jobs.Add(job); store.Save(State);
        await ConfigureFolder(job); store.Save(State);
    });
    public Task DeclineAsync(IncomingOffer offer) => Guard(() =>
    {
        if (!Offers.Contains(offer) || State.Jobs.Any(j => j.Id == offer.Id))
            throw new InvalidOperationException("邀請已變更，請重新選取。");
        State.DeclinedOffers.Add(OfferKey(offer.Id, offer.FriendId));
        store.Save(State);
        Offers = Offers.Where(o => o.Id != offer.Id || o.FriendId != offer.FriendId).ToArray();
        return Task.CompletedTask;
    });
    private static string OfferKey(string folder, string device) => folder + ":" + device;
    public Task SaveReceivedAsync(TransferJob job) => Guard(async () =>
    {
        if (!State.Jobs.Contains(job) || job.Kind != "receive" || job.Saved) throw new InvalidOperationException("請選取尚未獨立保存的單次收件。");
        if (!job.Detached)
        {
            try
            {
                var status = await engine.GetAsync("db/status?folder=" + job.Id);
                if (status["state"]?.GetValue<string>() != "idle" || (status["needTotalItems"]?.GetValue<long>() ?? 0) != 0 ||
                    (status["needBytes"]?.GetValue<long>() ?? 0) != 0 || !File.Exists(Path.Combine(job.Path, Snapshots.ManifestName)))
                    throw new IOException("檔案尚未完整接收，請稍後再保存。");
            }
            catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound && job.SavedPath != null && job.State == "正在隔離與驗證")
            { job.Detached = true; store.Save(State); }
        }
        var inbox = job.SavedPath == null ? State.Inbox : Path.GetDirectoryName(job.SavedPath)!;
        ValidateInbox(inbox);
        if (!string.Equals(Path.GetPathRoot(job.Path), Path.GetPathRoot(inbox), StringComparison.OrdinalIgnoreCase) &&
            (job.SavedPath == null || !Directory.Exists(job.SavedPath)))
        {
            var requiredBytes = await Task.Run(() => SafePaths.Walk(job.Path)
                .Where(File.Exists).Sum(path => new FileInfo(path).Length));
            ReceiveDiskPolicy.RequireRoom(inbox, requiredBytes);
        }
        // Journal the intended destination BEFORE revoking the share or moving data.
        job.SavedPath ??= Path.Combine(inbox, "Received-" + job.Id[8..]);
        job.State = "正在隔離與驗證"; store.Save(State);
        await RemoveFolder(job.Id);
        job.Detached = true; store.Save(State);
        await engine.WithStoppedAsync(async () =>
        {
            // Recovery for a crash after atomic rename but before committing the journal.
            if (Directory.Exists(job.SavedPath))
            {
                await Task.Run(() => Snapshots.VerifyAsync(job.SavedPath, job.Id));
            }
            else
            {
                await Task.Run(() => Snapshots.VerifyAsync(job.Path, job.Id));
                SafePaths.NoLinks(job.SavedPath);
                if (string.Equals(Path.GetPathRoot(job.Path), Path.GetPathRoot(job.SavedPath), StringComparison.OrdinalIgnoreCase))
                {
                    Directory.Move(job.Path, job.SavedPath); // No overwrite, no extra full copy on the same volume.
                }
                else
                {
                    // Keep source intact on cross-volume failure; unique staging path, then atomic rename.
                    var staging = job.SavedPath + ".saving";
                    if (Directory.Exists(staging)) throw new IOException("上次跨磁碟保存留下暫存，請先人工檢查 .saving 目錄後再重試。");
                    await Task.Run(async () =>
                    {
                        Directory.CreateDirectory(staging);
                        foreach (var item in SafePaths.Walk(job.Path))
                        {
                            var relative = Path.GetRelativePath(job.Path, item);
                            if (relative == ".stfolder" || relative.StartsWith(".stfolder" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                            var target = SafePaths.Relative(staging, relative);
                            if (Directory.Exists(item)) Directory.CreateDirectory(target);
                            else { Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(item, target, false); }
                        }
                        await Snapshots.VerifyAsync(staging, job.Id);
                    });
                    Directory.Move(staging, job.SavedPath);
                }
            }
            job.Saved = true; job.State = "已獨立保存"; job.Error = null; store.Save(State);
        });
    });
    public void ValidateInbox(string path)
    {
        path = Path.GetFullPath(path); SafePaths.NoLinks(path);
        if (path.StartsWith("\\\\") || new DriveInfo(Path.GetPathRoot(path)!).DriveFormat != "NTFS") throw new IOException("目前收件位置需為本機 NTFS 磁碟，以支援安全標記及可靠搬移。");
        if (new[] { Path.Combine(profile, "engine"), Path.Combine(profile, "Outbox"), Path.Combine(profile, "Incoming") }.Any(p => SafePaths.Overlap(path, p)) ||
            State.Jobs.Any(j => !j.Detached && SafePaths.Overlap(path, j.Path))) throw new IOException("收件位置不能與任何共享、設定或暫存資料夾重疊。");
        Directory.CreateDirectory(path);
    }
    private async Task RemoveFolder(string id)
    {
        try { await engine.WriteAsync("config/folders/" + id, null, "DELETE"); }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound) { }
    }
    public Task CancelAsync(TransferJob job) => Guard(async () =>
    {
        if (job.Saved) return;
        await RemoveFolder(job.Id);
        job.Detached = true;
        if (!job.CleanupStarted) job.State = "已停止共享 · 本機檔案保留";
        store.Save(State);
    });
    public string GetOutboxPath(TransferJob job)
    {
        if (!State.Jobs.Contains(job) || job.Kind != "send" || !FolderId().IsMatch(job.Id) ||
            !job.Id.StartsWith("nd-once-", StringComparison.Ordinal))
            throw new InvalidOperationException("請選取單次傳送的工作。");
        var path = SafePaths.Relative(Path.Combine(profile, "Outbox"), job.Id);
        if (!string.Equals(Path.GetFullPath(job.Path), path, StringComparison.OrdinalIgnoreCase))
            throw new IOException("暫存路徑不符合工作紀錄，已停止操作。");
        return path;
    }
    public Task ClearOutboxAsync(TransferJob job) => Guard(async () =>
    {
        var path = GetOutboxPath(job);
        if (!job.Detached) throw new InvalidOperationException("請先確認朋友已保存，再取消共享。取消後才可刪除暫存。");
        if (job.OutboxRemoved) return;

        var folders = (await engine.ReadAsync("config/folders"))!.AsArray();
        if (folders.Any(folder => folder?["id"]?.GetValue<string>() == job.Id ||
            (folder?["path"]?.GetValue<string>() is { Length: > 0 } folderPath && SafePaths.Overlap(folderPath, path))))
            throw new IOException("傳輸引擎仍在共享此位置，請先停止共享。");

        // Persist the intent before the first deletion so a partial cleanup cannot be re-shared.
        job.CleanupStarted = true;
        job.State = "暫存清理中 · 請勿重試傳送";
        job.Error = null;
        store.Save(State);
        try
        {
            await engine.WithStoppedAsync(() => Task.Run(() => DeleteOutboxTree(path)));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            job.State = "暫存清理未完成";
            job.Error = error.Message;
            store.Save(State);
            throw;
        }
        job.OutboxRemoved = true;
        job.State = "暫存已清理";
        store.Save(State);
    });
    private static void DeleteOutboxTree(string path)
    {
        SafePaths.NoLinks(path);
        if (File.Exists(path)) throw new IOException("預期暫存資料夾，但此位置是檔案。");
        if (!Directory.Exists(path)) return;

        // Validate the full tree before touching any entry. Delete only enumerated leaves.
        var entries = SafePaths.Walk(path).ToArray();
        for (var i = entries.Length - 1; i >= 0; i--)
        {
            var entry = entries[i];
            SafePaths.NoLinks(entry);
            if (Directory.Exists(entry)) Directory.Delete(entry);
            else File.Delete(entry);
        }
        Directory.Delete(path);
    }
    public Task RetryAsync(TransferJob job) => Guard(async () =>
    {
        if (job.CleanupStarted) throw new InvalidOperationException("暫存已開始清理，請重新選取原始檔案建立新工作。");
        if (job.Saved) throw new InvalidOperationException("檔案已獨立保存，不會重新共享。");
        await RequireFriend(job.FriendId);
        if (job.Kind == "send")
            await Task.Run(() => Snapshots.VerifyAsync(job.Path, job.Id));
        // A detached receiver remains isolated; use Save to retry preservation, not re-expose it.
        if (job.Detached && job.Kind == "receive") throw new InvalidOperationException("收件已隔離，請按「獨立保存」重試。");
        job.Paused = false; await ConfigureFolder(job); job.Detached = false; job.Error = null; job.State = "已恢復"; store.Save(State);
        await engine.WriteAsync("db/scan?folder=" + job.Id, null);
    });
    public Task ToggleAsync(TransferJob job) => Guard(async () =>
    {
        if (job.Detached || job.Saved) throw new InvalidOperationException("此共享已停止。");
        await RequireFriend(job.FriendId);
        var pause = !job.Paused;
        await engine.WriteAsync("config/folders/" + job.Id, new JsonObject { ["paused"] = pause }, "PATCH");
        job.Paused = pause; job.State = pause ? "已暫停" : "已恢復"; store.Save(State);
    });
    [GeneratedRegex("^nd-(once|live)-[a-f0-9]{32}$", RegexOptions.CultureInvariant)] private static partial Regex FolderId();
}
