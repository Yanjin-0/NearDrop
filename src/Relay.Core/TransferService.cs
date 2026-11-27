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
    public void SetInbox(string path)
    {
        ValidateInbox(path);
        SavePreference(State.Inbox, Path.GetFullPath(path), value => State.Inbox = value);
    }
    public void SetKeepBackground(bool enabled) => SavePreference(State.KeepBackground, enabled, value => State.KeepBackground = value);
    public void SetReceiveNotifications(bool enabled) => SavePreference(State.ReceiveNotifications, enabled, value => State.ReceiveNotifications = value);
    public void SetExplorerHotkey(bool enabled) => SavePreference(State.ExplorerHotkey, enabled, value => State.ExplorerHotkey = value);

    private void SavePreference<T>(T previous, T next, Action<T> apply)
    {
        apply(next);
        try { store.Save(State); }
        catch { apply(previous); throw; }
    }
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
            var paths = sources.Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (Snapshots.FindConflictingTopLevelName(paths) is string conflict)
                throw new IOException($"有多個項目名為「{conflict}」。請分批傳送。");
            foreach (var source in paths)
            {
                SafePaths.NoLinks(source);
                if (Path.GetPathRoot(source) == source) throw new IOException("請選取磁碟內的檔案或資料夾，不能傳送整個磁碟。");
                // Never send engine credentials, queues or their ancestors.
                if (SafePaths.Overlap(source, Path.Combine(profile, "engine")) || SafePaths.Overlap(source, Path.Combine(profile, "Outbox")) || SafePaths.Overlap(source, Path.Combine(profile, "Incoming")))
                    throw new IOException("請選取一般檔案，不能傳送應用程式設定或傳輸暫存。");
            }
            var job = new TransferJob { Id = "nd-once-" + Guid.NewGuid().ToString("N"), FriendId = friend.DeviceId, FriendName = friend.Name,
                Label = paths.Length == 1 ? Path.GetFileName(paths[0]) : $"{paths.Length} 個項目", Kind = "send" };
            job.Path = Path.Combine(profile, "Outbox", job.Id);
            State.Jobs.Add(job); store.Save(State); result = job;
            var publicationStarted = false;
            try
            {
                job.TotalBytes = await Task.Run(() => Snapshots.CreateAsync(paths, job.Path, job.Id, ct, progress), ct);
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
            catch (Exception e)
            {
                job.Detached = !publicationStarted;
                job.State = publicationStarted ? "建立傳送共享失敗" : TransferJob.PreparationFailed;
                job.Error = e.Message; store.Save(State); throw;
            }
        });
        return result!;
    }
    public async Task<TransferJob> WatchAsync(string source, Friend friend)
    {
        TransferJob? result = null;
        await Guard(async () =>
        {
            await RequireFriend(friend.DeviceId);
            source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
            ValidateWatchSource(source);
            // Initial traversal is validation only. Syncthing's native watcher handles later changes.
            await Task.Run(() => { foreach (var _ in SafePaths.Walk(source)) { } });
            var job = new TransferJob { Id = "nd-live-" + Guid.NewGuid().ToString("N"), FriendId = friend.DeviceId,
                FriendName = friend.Name, Label = Path.GetFileName(source), Path = source, Kind = "watch-send", State = "已排入持續同步" };
            State.Jobs.Add(job); store.Save(State); result = job;
            await EstablishSharing(job); store.Save(State);
        });
        return result!;
    }
    private void ValidateWatchSource(string source, TransferJob? current = null)
    {
        SafePaths.NoLinks(source);
        if (Path.GetPathRoot(source) == source) throw new IOException("請選取磁碟內的專用資料夾，不能同步整個磁碟。");
        if (!Directory.Exists(source)) throw new IOException("找不到原本的同步來源資料夾。請還原該位置，或新增使用其他路徑的同步工作。");
        foreach (var sensitive in new[] { profile, State.Inbox, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppContext.BaseDirectory })
            if (sensitive.Length > 0 && SafePaths.Overlap(source, sensitive)) throw new IOException("不能監控系統、設定、收件或其上層資料夾，請選用專用的傳送資料夾。");
        if (State.Jobs.Any(j => j != current && !j.Detached && SafePaths.Overlap(j.Path, source)))
            throw new IOException("此來源與另一筆共享工作的資料夾重疊。請先停止重疊的工作。");
    }
    private async Task RequireFriend(string id)
    {
        var list = await new PairingService(engine).ListAsync();
        if (list.All(f => f.DeviceId != id)) throw new InvalidOperationException("朋友已解除配對；不能建立或恢復共享。");
    }
    private async Task ConfigureFolder(TransferJob job, bool? paused = null)
    {
        await RequireFriend(job.FriendId);
        SafePaths.NoLinks(job.Path);
        var folder = await engine.GetAsync("config/defaults/folder");
        folder["id"] = job.Id; folder["label"] = job.Label; folder["path"] = job.Path;
        folder["type"] = job.Kind.EndsWith("send") ? "sendonly" : "receiveonly";
        folder["paused"] = paused ?? job.Paused;
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
    private async Task EstablishSharing(TransferJob job)
    {
        try { await ConfigureFolder(job); }
        catch (Exception e) when (e is IOException or HttpRequestException or UnauthorizedAccessException or InvalidOperationException)
        {
            job.State = "未能確認共享已建立";
            job.Error = e.Message;
            store.Save(State);
            throw;
        }
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
        foreach (var job in State.Jobs.Where(j => !j.Saved))
        {
            // Refresh runs outside the save operation. A journal without a committed save is recoverable.
            if (job.SavedPath != null)
            {
                if (job.State is not (TransferJob.SaveFailed or TransferJob.SaveRejected)) job.State = TransferJob.SaveInterrupted;
                continue;
            }
            if (job.Detached) continue;
            try
            {
                // Older profiles recorded a peer confirmation only in the displayed state.
                if (job.HasConfirmedPeerSync) job.PeerSyncConfirmed = true;
                var config = configured.FirstOrDefault(f => f!["id"]!.GetValue<string>() == job.Id);
                if (config == null)
                {
                    if (job.IsAwaitingFirstSave)
                    {
                        // The share may have been removed before its stopped state was recorded.
                        job.Detached = true;
                        job.Paused = false;
                        job.State = TransferJob.DetachedAwaitingSave;
                    }
                    else if (job.Kind == "send" && job.State is ("準備中" or TransferJob.PreparationInterrupted))
                    {
                        job.State = TransferJob.PreparationInterrupted;
                        job.Detached = true;
                    }
                    else job.State = TransferJob.ShareMissing;
                    continue;
                }
                var friend = friends.FirstOrDefault(f => f.DeviceId == job.FriendId);
                if (friend == null || !config["devices"]!.AsArray().Any(d => d!["deviceID"]!.GetValue<string>() == job.FriendId))
                {
                    // An already completed local receipt can still be saved after a pairing is removed.
                    if (!job.IsAwaitingFirstSave) job.State = TransferJob.FriendAccessRevoked;
                    job.Error = null;
                    continue;
                }
                job.Paused = config["paused"]?.GetValue<bool>() ?? job.Paused;
                if (job.Paused) { job.State = "本機已暫停同步"; job.Error = null; job.LastProgressAt = default; continue; }
                if (job.HasConfirmedPeerSync) { job.State = TransferJob.SenderSynced; job.Error = null; continue; }
                var outgoing = job.Kind.EndsWith("send", StringComparison.Ordinal);
                if (!outgoing)
                {
                    try { ReceiveDiskPolicy.RequireRoom(job.Path); }
                    catch (IOException e) { job.State = "接收磁碟檢查未通過"; job.Error = e.Message; continue; }
                }
                var status = await engine.GetAsync("db/status?folder=" + job.Id);
                var folderState = status["state"]?.GetValue<string>();
                if (folderState == "error" || HasFolderErrors(status))
                { job.State = "同步遇到問題"; job.Error = "請確認資料夾可存取、檔案權限與可用空間，再重試。"; continue; }
                var readyToSave = job.Kind == "receive" && IsReceiptComplete(job, status);
                var remote = friend.Connected && !friend.Paused && !readyToSave
                    ? await engine.GetAsync("db/completion?folder=" + job.Id + "&device=" + job.FriendId) : null;
                var state = GetSyncState(job, friend, status, remote, outgoing, readyToSave);
                if (job.State == TransferJob.SaveFailed && state == TransferJob.AwaitingSave) continue;
                job.Error = null;
                if (state == TransferJob.SenderSynced) job.PeerSyncConfirmed = true;
                job.State = state;
            }
            catch (Exception e) when (e is HttpRequestException or IOException) { job.Error = e.Message; job.State = "暫時無法取得狀態"; }
        }
        store.Save(State);
    });
    private static bool HasFolderErrors(JsonObject status) =>
        (status["errors"]?.GetValue<int>() ?? 0) > 0 || (status["pullErrors"]?.GetValue<int>() ?? 0) > 0;

    private static bool HasNoPendingItems(JsonObject progress, string itemsKey) =>
        progress["needBytes"]?.GetValue<long>() == 0 && progress[itemsKey]?.GetValue<long>() == 0 &&
        progress["needDeletes"]?.GetValue<long>() == 0;

    private static bool IsReceiptComplete(TransferJob job, JsonObject status) =>
        status["state"]?.GetValue<string>() == "idle" && !HasFolderErrors(status) &&
        HasNoPendingItems(status, "needTotalItems") && File.Exists(Path.Combine(job.Path, Snapshots.ManifestName));

    private static string GetSyncState(TransferJob job, Friend friend, JsonObject status, JsonObject? remote, bool outgoing, bool readyToSave)
    {
        var progress = outgoing && remote != null ? remote : status;
        var folderState = status["state"]?.GetValue<string>();
        var remoteState = remote?["remoteState"]?.GetValue<string>();
        var connected = friend.Connected && !friend.Paused;
        var currentProgress = connected && remoteState == "valid";
        job.RecentBytesPerSecond = 0;
        if (progress["globalBytes"]?.GetValue<long>() is >= 0 and long total &&
            progress["needBytes"]?.GetValue<long>() is >= 0 and long need && (!outgoing || currentProgress))
        {
            var now = DateTime.UtcNow;
            var done = Math.Clamp(total - need, 0, total);
            if (currentProgress && total == job.TotalBytes && job.LastProgressAt != default)
                job.RecentBytesPerSecond = Math.Max(0, done - job.DoneBytes) / Math.Max(0.1, (now - job.LastProgressAt).TotalSeconds);
            job.LastProgressAt = currentProgress ? now : default;
            job.TotalBytes = total;
            job.DoneBytes = done;
        }
        else job.LastProgressAt = default;

        var idle = folderState == "idle";
        var complete = idle && HasNoPendingItems(progress, outgoing ? "needItems" : "needTotalItems");
        var hasIndex = (progress[outgoing ? "globalItems" : "globalTotalItems"]?.GetValue<long>() ?? 0) > 0;
        if (readyToSave) return TransferJob.AwaitingSave;
        if (friend.Paused) return "本機已暫停與此裝置連線";
        if (!connected) return "等待與對方裝置連線";
        if (remoteState == "paused") return "對方已暫停同步";
        if (remoteState == "notSharing") return "對方目前未啟用此共享";
        if (remoteState != "valid") return "正在確認對方的共享狀態";
        if (folderState is "scanning" or "scan-waiting") return "本機正在檢查資料夾變更";
        if (complete && job.Kind == "send" && hasIndex) return TransferJob.SenderSynced;
        if (complete && job.Kind == "watch-send") return "對方已同步 · 持續傳送";
        if (idle && job.Kind == "watch-receive" && progress["receiveOnlyTotalItems"]?.GetValue<long>() > 0)
            return "本機有變更 · 不會傳回對方";
        if (complete && job.Kind == "watch-receive")
            return hasIndex ? "已同步至目前收到的版本 · 持續接收" : "目前未收到同步項目 · 持續接收";
        if (folderState == "syncing" || job.RecentBytesPerSecond > 0) return $"正在同步 · {friend.ConnectionLabel}";
        return "已連線 · 等待同步";
    }
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
        Offers = Offers.Where(item => item.Id != offer.Id).ToArray();
        await EstablishSharing(job); store.Save(State);
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
        if (!State.Jobs.Contains(job) || !job.CanSave) throw new InvalidOperationException("請選取待獨立保存或可重試保存的單次收件。");
        try { await SaveReceivedCoreAsync(job); }
        catch (Exception e) when (!job.Saved && job.State != TransferJob.SaveRejected &&
            e is (IOException or HttpRequestException or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException))
        {
            job.State = TransferJob.SaveFailed;
            job.Error = e.Message;
            store.Save(State);
            throw;
        }
    });
    private async Task SaveReceivedCoreAsync(TransferJob job)
    {
        if (!job.Detached && job.SavedPath != null)
        {
            // Deleting the share may have succeeded before the journal recorded the detachment.
            try { await engine.GetAsync("config/folders/" + job.Id); }
            catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound)
            { job.Detached = true; store.Save(State); }
        }
        if (!job.Detached)
        {
            var status = await engine.GetAsync("db/status?folder=" + job.Id);
            if (!IsReceiptComplete(job, status))
                throw new IOException("目前無法確認收件完整。請等候同步完成後再保存。");
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
        job.State = "正在隔離與驗證"; job.Error = null; store.Save(State);
        await RemoveFolder(job.Id);
        job.Detached = true; store.Save(State);
        await engine.WithStoppedAsync(async () =>
        {
            // Recovery for a crash after atomic rename but before committing the journal.
            if (Directory.Exists(job.SavedPath))
            {
                if (Directory.Exists(job.Path))
                {
                    try { await Task.Run(() => Snapshots.VerifyAsync(job.SavedPath, job.Id)); }
                    catch (Exception e) when (e is SnapshotIntegrityException or FileNotFoundException or DirectoryNotFoundException)
                    { throw new IOException("預定保存位置已有無法驗證的內容。請檢查並移開該資料夾後重試。", e); }
                }
                else await VerifyReceiptForSave(job, job.SavedPath);
            }
            else
            {
                await VerifyReceiptForSave(job, job.Path);
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
    }
    private async Task VerifyReceiptForSave(TransferJob job, string path)
    {
        try { await Task.Run(() => Snapshots.VerifyAsync(path, job.Id)); }
        catch (SnapshotIntegrityException e)
        {
            job.State = TransferJob.SaveRejected;
            job.Error = e.Message;
            store.Save(State);
            throw new IOException(e.Message + " 請對方建立新的傳送邀請。", e);
        }
    }
    public void ValidateInbox(string path)
    {
        path = Path.GetFullPath(path);
        var drive = path.StartsWith(@"\\", StringComparison.Ordinal) ? null : new DriveInfo(Path.GetPathRoot(path)!);
        if (drive == null || drive.DriveType == DriveType.Network || drive.DriveFormat != "NTFS")
            throw new IOException("單次收件須保存到本機 NTFS 磁碟，以保留 Windows 安全標記。");
        SafePaths.NoLinks(path);
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
        if (!State.Jobs.Contains(job)) throw new InvalidOperationException("找不到這筆傳輸工作。");
        if (job.Saved) return;
        if (job.SavedPath != null) throw new InvalidOperationException("請先完成這筆收件的獨立保存。");
        var keepReadyReceipt = job.CanSave;
        if (keepReadyReceipt && !job.Detached)
        {
            var status = await engine.GetAsync("db/status?folder=" + job.Id);
            if (!IsReceiptComplete(job, status))
                throw new IOException("收件狀態已變更。請重新確認接收進度後再停止共享。");
        }
        await RemoveFolder(job.Id);
        if (job.HasConfirmedPeerSync) job.PeerSyncConfirmed = true;
        job.Detached = true;
        job.Paused = false;
        if (!job.CleanupStarted)
        {
            job.State = keepReadyReceipt ? TransferJob.DetachedAwaitingSave : "已停止共享 · 本機檔案保留";
            job.Error = null;
        }
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
        if (job.HasConfirmedPeerSync) job.PeerSyncConfirmed = true;
        job.CleanupStarted = true;
        job.State = "暫存清理中 · 請勿重試傳送";
        job.Error = null;
        store.Save(State);
        try
        {
            await engine.WithStoppedAsync(async () =>
            {
                await Task.Run(() => DeleteOutboxTree(path));
                job.OutboxRemoved = true;
                job.State = "暫存已清理";
                store.Save(State);
            });
        }
        catch (Exception error) when (!job.OutboxRemoved && error is (IOException or UnauthorizedAccessException))
        {
            job.State = "暫存清理未完成";
            job.Error = error.Message;
            store.Save(State);
            throw;
        }
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
        if (!State.Jobs.Contains(job)) throw new InvalidOperationException("找不到這筆傳輸工作。");
        if (job.CleanupStarted) throw new InvalidOperationException("暫存已開始清理，請重新選取原始檔案建立新工作。");
        if (job.Saved) throw new InvalidOperationException("檔案已獨立保存，不會重新共享。");
        if (job.State is TransferJob.PreparationInterrupted or TransferJob.PreparationFailed) throw new InvalidOperationException(job.State);
        if (job.HasConfirmedPeerSync) throw new InvalidOperationException("已確認對方同步。需要再傳送時，請建立新的傳送邀請。");
        if (job.CanSave)
            throw new InvalidOperationException("請在接收頁選取這筆收件並執行獨立保存。");
        if (job.Kind == "receive" && job.Detached)
            throw new InvalidOperationException("這筆收件已停止。若仍需接收，請對方建立新的傳送邀請。");
        try
        {
            if (job.Kind == "watch-send") ValidateWatchSource(job.Path, job);
            else SafePaths.NoLinks(job.Path);
            if (job.Kind == "send") await Task.Run(() => Snapshots.VerifyAsync(job.Path, job.Id));
            if (await CanReuseFolder(job))
            {
                if (job.Paused)
                    await engine.WriteAsync("config/folders/" + job.Id, new JsonObject { ["paused"] = false }, "PATCH");
                job.State = "正在要求重新掃描";
            }
            else
            {
                await ConfigureFolder(job, paused: false);
                job.State = "已啟用共享 · 等待同步";
            }
            job.Paused = false; job.Detached = false; job.Error = null;
            store.Save(State);
            await engine.WriteAsync("db/scan?folder=" + job.Id, null);
            if (job.State == "正在要求重新掃描")
            {
                job.State = "已要求重新掃描 · 等待更新狀態";
                store.Save(State);
            }
        }
        catch (Exception e) when (e is IOException or HttpRequestException or UnauthorizedAccessException or InvalidOperationException)
        {
            job.Error = e.Message;
            store.Save(State);
            throw;
        }
    });
    private async Task<bool> CanReuseFolder(TransferJob job)
    {
        await RequireFriend(job.FriendId);
        if (job.Detached) return false;
        JsonObject folder;
        try { folder = await engine.GetAsync("config/folders/" + job.Id); }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound) { return false; }
        var configuredPath = folder["path"]?.GetValue<string>();
        var expectedType = job.Kind.EndsWith("send", StringComparison.Ordinal) ? "sendonly" : "receiveonly";
        var devices = folder["devices"]!.AsArray().Select(device => device!["deviceID"]!.GetValue<string>()).ToArray();
        if (configuredPath == null || !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredPath)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(job.Path)), StringComparison.OrdinalIgnoreCase) ||
            folder["type"]?.GetValue<string>() != expectedType ||
            devices.Any(device => device != engine.DeviceId && device != job.FriendId))
            throw new IOException("引擎的共享設定與工作紀錄不符。請先停止共享，再重新啟用。");
        job.Paused = folder["paused"]?.GetValue<bool>() ?? job.Paused;
        return devices.Contains(engine.DeviceId) && devices.Contains(job.FriendId);
    }
    public Task ToggleAsync(TransferJob job) => Guard(async () =>
    {
        if (!State.Jobs.Contains(job) || !job.CanToggleWatch) throw new InvalidOperationException("這筆工作目前無法暫停或繼續同步。");
        await RequireFriend(job.FriendId);
        var pause = !job.Paused;
        if (!pause && job.Kind == "watch-send") ValidateWatchSource(job.Path, job);
        await engine.WriteAsync("config/folders/" + job.Id, new JsonObject { ["paused"] = pause }, "PATCH");
        job.Paused = pause; job.Error = null;
        job.State = pause ? "本機已暫停同步" : "已繼續同步 · 等待更新狀態";
        store.Save(State);
    });
    [GeneratedRegex("^nd-(once|live)-[a-f0-9]{32}$", RegexOptions.CultureInvariant)] private static partial Regex FolderId();
}
