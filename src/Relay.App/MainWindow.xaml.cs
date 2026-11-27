using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Relay.Core;
using Relay.Windows;
using Forms = System.Windows.Forms;

namespace Relay.App;

public partial class MainWindow : Window
{
    private readonly SyncthingEngine engine;
    private readonly PairingService pairing;
    private readonly TransferService transfers;
    private readonly string profile;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private Forms.NotifyIcon? tray;
    private readonly SemaphoreSlim actions = new(1);
    private Forms.ToolStripMenuItem? receiveMenu;
    private string? balloonTarget;
    private readonly Dictionary<TabItem, string> pageNotices = [];
    private bool ready, refreshing, exiting, busy;
    private bool updatingTransferLists;
    private readonly ObservableCollection<SendSelection> sendSelection = [];
    private CancellationTokenSource? preparation;
    private bool publishing;
    private HwndSource? source;
    private bool hotkey;
    private bool restoringPreference;
    public MainWindow(string profile, bool localTest)
    {
        InitializeComponent(); this.profile = profile;
        engine = new SyncthingEngine(Path.Combine(AppContext.BaseDirectory, "engine", "syncthing.exe"), Path.Combine(profile, "engine"), localTest);
        pairing = new PairingService(engine);
        transfers = new TransferService(engine, profile);
        KeepBackground.IsChecked = transfers.State.KeepBackground;
        KeepBackground.Checked += ChangeKeepBackground;
        KeepBackground.Unchecked += ChangeKeepBackground;
        InboxPath.Text = transfers.State.Inbox;
        ReceiveNotifications.IsChecked = transfers.State.ReceiveNotifications;
        ReceiveNotifications.Checked += ChangeReceiveNotifications;
        ReceiveNotifications.Unchecked += ChangeReceiveNotifications;
        DraftItems.ItemsSource = sendSelection;
        StartAtLogin.IsChecked = !localTest && ExplorerIntegration.StartupEnabled;
        StartAtLogin.IsEnabled = !localTest;
        FriendName.TextChanged += (_, _) => UpdateActions();
        FriendId.TextChanged += (_, _) => UpdateActions();
        Verified.Checked += (_, _) => UpdateActions();
        Verified.Unchecked += (_, _) => UpdateActions();
        timer.Tick += async (_, _) => { if (!busy) await RefreshAsync(); };
        UpdateHistory();
        UpdateActions();
    }
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (tray != null) return;
        tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = "NearDrop · 安心傳檔", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("開啟 NearDrop", null, (_, _) => Dispatcher.Invoke(() => { Show(); WindowState = WindowState.Normal; Activate(); }));
        receiveMenu = new Forms.ToolStripMenuItem("開啟接收頁");
        receiveMenu.Click += (_, _) => Dispatcher.InvokeAsync(() => OpenReceiving());
        menu.Items.Add(receiveMenu);
        menu.Items.Add("退出並停止傳輸", null, (_, _) => Dispatcher.InvokeAsync(ExitAsync));
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(() => { Show(); WindowState = WindowState.Normal; Activate(); });
        tray.BalloonTipClicked += (_, _) => Dispatcher.InvokeAsync(() => OpenReceiving(balloonTarget));
        await Run(async () =>
        {
            await engine.StartAsync(); MyId.Text = engine.DeviceId;
            ready = true; EngineStatus.Text = "傳輸引擎已啟動";
            await RefreshAsync(); timer.Start();
            if (Tabs.SelectedItem == DeviceTab && SendFriends.Items.Count > 0) Tabs.SelectedItem = SendTab;
        });
        if (!ready) EngineStatus.Text = "傳輸引擎未啟動";
        source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        source?.AddHook(WindowMessage);
        ExplorerHotkey.IsChecked = transfers.State.ExplorerHotkey;
        ExplorerHotkey.Checked += EnableHotkey;
        ExplorerHotkey.Unchecked += EnableHotkey;
        if (transfers.State.ExplorerHotkey) ApplyHotkey(true);
    }
    private async Task Run(Func<Task> action, bool disableWindow = true)
    {
        if (preparation != null)
        {
            SetNotice("正在準備另一筆傳送，請等待完成或從傳送頁取消。");
            return;
        }
        await actions.WaitAsync();
        if (exiting) { actions.Release(); return; }
        busy = true;
        if (disableWindow) IsEnabled = false;
        try { while (refreshing) await Task.Delay(50); SetNotice(""); await action(); }
        catch (Exception e) { SetNotice(e.Message); }
        finally { busy = false; IsEnabled = true; UpdateActions(); actions.Release(); }
    }
    private void TabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.Source, Tabs)) ShowPageNotice();
    }
    private void SetNotice(string message, TabItem? page = null)
    {
        page ??= Tabs.SelectedItem as TabItem;
        if (page == null) return;
        pageNotices[page] = message;
        if (ReferenceEquals(page, Tabs.SelectedItem)) ShowPageNotice();
    }
    private void ShowPageNotice()
    {
        if (Tabs == null || PageMessage == null) return;
        var message = Tabs.SelectedItem is TabItem page && pageNotices.TryGetValue(page, out var value) ? value : "";
        Notice.Text = message;
        PageMessage.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
    private void ActionSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateActions();
    private void GoToPairing(object sender, RoutedEventArgs e)
    {
        Tabs.SelectedItem = DeviceTab;
        FriendName.Focus();
    }
    private void UpdateActions()
    {
        if (updatingTransferLists || !IsInitialized || ClearOutboxButton == null || RemoveFriendButton == null) return;
        var haveFriends = SendFriends.Items.Count > 0;
        NoFriends.Visibility = ready && !haveFriends ? Visibility.Visible : Visibility.Collapsed;
        var preparing = preparation != null;
        var duplicateName = Snapshots.FindConflictingTopLevelName(sendSelection.Select(item => item.FullPath));
        SendButton.IsEnabled = ready && !preparing && duplicateName == null && sendSelection.Count > 0 && SendFriends.SelectedItem is Friend;
        RemoveDraftButton.IsEnabled = !preparing && DraftItems.SelectedItem is SendSelection;
        ClearDraftButton.IsEnabled = !preparing && sendSelection.Count > 0;
        ShowLocalPath(DraftLocalPath, (DraftItems.SelectedItem as SendSelection)?.FullPath);
        PickFilesButton.IsEnabled = PickFolderButton.IsEnabled = !preparing;
        DraftPanel.Visibility = sendSelection.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SendHint.Text = preparing ? "你可以切換頁面查看其他工作。"
            : !ready ? "傳輸引擎尚未就緒。" : sendSelection.Count == 0 ? "先選取檔案或資料夾。"
            : duplicateName != null ? $"有多個項目名為「{duplicateName}」。請移除其中一個，分批傳送。"
            : !haveFriends ? "先新增一位朋友。已選取的檔案會保留。"
            : SendFriends.SelectedItem is not Friend ? "選擇一位收件人。" : "傳送會建立暫存副本，約需一份額外磁碟空間。";

        var offer = Incoming.SelectedItem as IncomingOffer;
        var received = Incoming.SelectedItem as TransferJob;
        SetAction(AcceptButton, ready && offer != null);
        SetAction(DeclineButton, ready && offer != null);
        SetAction(SaveButton, ready && received?.CanSave == true);
        var retrySave = received is { CanSave: true, IsAwaitingFirstSave: false };
        SaveButton.Content = retrySave ? "重試獨立保存" : "保存到收件資料夾";
        SetAction(OpenReceivedButton, received?.Saved == true);
        SetAction(OpenReceivedWatchButton, received?.Kind == "watch-receive");
        IncomingHint.Text = offer != null ? (offer.Continuous ? "接受後，來源的修改與刪除會同步。" : "接受這次邀請後即可接收檔案。")
            : received?.Saved == true ? "已保存。可以開啟檔案所在資料夾。"
            : received?.State == TransferJob.SaveRejected ? "這份收件無法獨立保存。請對方建立新的傳送邀請。"
            : retrySave ? "保存尚未完成。請確認保存位置和工作列所列原因後重試。"
            : received is { IsAwaitingFirstSave: true, Detached: true } ? "共享已停止。這份已接收的檔案仍可獨立保存。"
            : received?.CanSave == true ? "接收完成。保存後，這份檔案會脫離共享。"
            : received?.Kind == "watch-receive" ? "選擇「開啟同步資料夾」查看目前已收到的檔案。"
            : received?.Detached == true ? "這筆收件已停止。若仍需接收，請對方建立新的傳送邀請。"
            : received != null ? "接收狀態顯示在選取的工作上。" : "選取一筆邀請或收件查看可用操作。";
        var receivedPath = received is { Saved: true, SavedPath: not null } ? SavedFilesPath(received.SavedPath)
            : retrySave ? received?.SavedPath ?? transfers.State.Inbox
            : received?.Kind == "watch-receive" ? received.Path : null;
        ShowLocalPath(IncomingLocalPath, receivedPath);

        var watch = Watches.SelectedItem as TransferJob;
        AddWatchButton.IsEnabled = ready && haveFriends;
        OpenWatchButton.IsEnabled = watch is { Kind: "watch-send" or "watch-receive" };
        ToggleWatchButton.IsEnabled = ready && (watch?.CanToggleWatch == true || CanReenableWatch(watch));
        StopWatchButton.IsEnabled = ready && watch?.CanStop == true;
        ToggleWatchButton.Content = CanReenableWatch(watch) ? "重新啟用共享" : watch?.Paused == true ? "繼續同步" : "暫停";
        ShowLocalPath(WatchLocalPath, watch?.Path);
        var job = History.SelectedItem as TransferJob;
        ViewIncomingButton.IsEnabled = job?.Kind is "receive" or "watch-receive";
        ViewIncomingButton.Content = job?.CanSave == true ? "前往保存" : "前往此筆收件";
        StopTransferButton.IsEnabled = ready && job?.CanStop == true;
        StopTransferButton.Content = job == null ? "停止這次傳送" : StopLabel(job);
        RetryButton.IsEnabled = ready && job?.CanRetry == true;
        RetryButton.Content = job?.CanToggleWatch == true && job.Paused ? "繼續同步"
            : NeedsSharing(job) ? "重新啟用共享"
            : job?.Error != null ? "重試同步" : "重新掃描";
        OpenOutboxButton.IsEnabled = job is { Kind: "send", OutboxRemoved: false };
        ClearOutboxButton.IsEnabled = ready && job is { Kind: "send", Detached: true, OutboxRemoved: false };
        CopyIdButton.IsEnabled = engine?.DeviceId.Length > 0;
        AddFriendButton.IsEnabled = ready && Verified.IsChecked == true && !string.IsNullOrWhiteSpace(FriendName.Text) && !string.IsNullOrWhiteSpace(FriendId.Text);
        RemoveFriendButton.IsEnabled = ready && Friends.SelectedItem is Friend;
    }
    private static bool NeedsSharing(TransferJob? job) =>
        job?.Detached == true || job?.State is TransferJob.ShareMissing or TransferJob.FriendAccessRevoked;
    private static bool CanReenableWatch(TransferJob? job) => job?.CanRetry == true && NeedsSharing(job);
    private static string StopLabel(TransferJob job) => job.Kind switch
    {
        "watch-send" or "watch-receive" => "停止持續同步",
        "send" when job.HasConfirmedPeerSync => "停止共享",
        "receive" when job.CanSave => "停止共享",
        "receive" => "停止這次接收",
        _ => "停止這次傳送"
    };
    private static void SetAction(Button button, bool available)
    {
        button.IsEnabled = available;
        button.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
    }
    private static void ShowLocalPath(TextBox field, string? path)
    {
        path ??= "";
        if (field.Text != path) field.Text = path;
        field.Visibility = string.IsNullOrWhiteSpace(path) ? Visibility.Collapsed : Visibility.Visible;
    }
    private async Task RefreshAsync()
    {
        if (!ready || refreshing || exiting) return;
        refreshing = true;
        try
        {
            var friends = await pairing.ListAsync();
            var selected = (SendFriends.SelectedItem as Friend)?.DeviceId;
            var managed = (Friends.SelectedItem as Friend)?.DeviceId;
            Friends.ItemsSource = friends; SendFriends.ItemsSource = friends;
            SendFriends.SelectedItem = friends.FirstOrDefault(f => f.DeviceId == selected);
            Friends.SelectedItem = friends.FirstOrDefault(f => f.DeviceId == managed);
            if (SendFriends.SelectedItem == null && friends.Count == 1) SendFriends.SelectedIndex = 0;
            await transfers.RefreshAsync();
            var incomingId = IncomingSelectionId(Incoming.SelectedItem);
            var watchId = (Watches.SelectedItem as TransferJob)?.Id;
            var received = transfers.State.Jobs.Where(j => j.Kind.EndsWith("receive", StringComparison.Ordinal))
                .OrderByDescending(j => j.CanSave).ThenByDescending(j => j.Created).ToArray();
            var incoming = transfers.Offers.Cast<object>().Concat(received).ToArray();
            updatingTransferLists = true;
            Incoming.ItemsSource = incoming;
            Incoming.SelectedItem = incomingId == null ? null : incoming.FirstOrDefault(i => IncomingSelectionId(i) == incomingId);
            var jobs = transfers.State.Jobs.OrderByDescending(j => j.Created).ToArray();
            UpdateHistory();
            Watches.ItemsSource = jobs.Where(j => j.Kind.StartsWith("watch")).ToArray(); Watches.SelectedItem = jobs.FirstOrDefault(j => j.Id == watchId);
            UpdateReceivingAttention(received);
            EngineStatus.Text = friends.Count == 0 ? "傳輸引擎已啟動" : $"已連線 {friends.Count(f => f.Connected && !f.Paused)}／{friends.Count} 台裝置";
        }
        catch (Exception)
        {
            if (engine.Running) EngineStatus.Text = "狀態更新失敗 · 顯示上次結果";
            else
            {
                ready = false;
                timer.Stop();
                EngineStatus.Text = "傳輸引擎已停止 · 請重新開啟 NearDrop";
            }
        }
        finally { updatingTransferLists = false; refreshing = false; UpdateActions(); }
    }
    private void HistoryFilterChanged(object sender, RoutedEventArgs e) => UpdateHistory();
    private void ResetHistoryFilters(object sender, RoutedEventArgs e)
    {
        ClearHistoryFilters();
        HistorySearch.Focus();
    }
    private void ClearHistoryFilters()
    {
        HistorySearch.Clear();
        HistoryStatusFilter.SelectedIndex = 0;
        HistoryDirectionFilter.SelectedIndex = 0;
    }
    private void UpdateHistory()
    {
        if (transfers == null || HistoryResults == null) return;
        var status = (HistoryStatus)Math.Max(0, HistoryStatusFilter.SelectedIndex);
        var direction = (HistoryDirection)Math.Max(0, HistoryDirectionFilter.SelectedIndex);
        var results = TransferHistory.Search(transfers.State.Jobs, HistorySearch.Text, status, direction);
        if (!History.Items.Cast<TransferJob>().SequenceEqual(results))
        {
            var selected = (History.SelectedItem as TransferJob)?.Id;
            History.ItemsSource = results;
            History.SelectedItem = selected == null ? null : results.FirstOrDefault(job => job.Id == selected);
        }
        foreach (var job in results) job.RefreshHistoryDisplay();
        HistoryResults.Text = transfers.State.Jobs.Count == 0 ? "尚無傳輸紀錄。"
            : results.Length == 0 ? "沒有符合的紀錄。可清除搜尋與篩選條件。"
            : $"顯示 {results.Length} 筆，共 {transfers.State.Jobs.Count} 筆";
        ResetHistoryButton.IsEnabled = status != HistoryStatus.All || direction != HistoryDirection.All || HistorySearch.Text.Length > 0;
        UpdateActions();
    }
    private static string? IncomingKey(object? item) => item switch
    {
        IncomingOffer offer => $"offer/{offer.FriendId}/{offer.Id}",
        TransferJob job => $"save/{job.FriendId}/{job.Id}",
        _ => null
    };
    private static (string FriendId, string Id)? IncomingSelectionId(object? item) => item switch
    {
        IncomingOffer offer => (offer.FriendId, offer.Id),
        TransferJob job => (job.FriendId, job.Id),
        _ => null
    };
    private void UpdateReceivingAttention(TransferJob[] received)
    {
        var invites = transfers.Offers;
        var awaiting = received.Where(job => job.CanSave).ToArray();
        var retrying = awaiting.Count(job => !job.IsAwaitingFirstSave);
        var count = invites.Count + awaiting.Length;
        ReceiveTab.Header = count == 0 ? "接收" : $"接收（{count}）";
        IncomingSummary.Text = count == 0 ? "目前沒有待接受或待保存的收件。"
            : $"{invites.Count} 筆待接受 · {awaiting.Length - retrying} 筆待保存" +
                (retrying == 0 ? "" : $" · {retrying} 筆待重試保存");
        NextIncomingButton.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (receiveMenu != null) receiveMenu.Text = count == 0 ? "開啟接收頁" : $"開啟接收頁（{count} 筆待處理）";
        if (!transfers.State.ReceiveNotifications || tray == null) return;

        var newInvites = invites.Where(offer => transfers.State.AnnouncedIncoming.Add(IncomingKey(offer)!)).ToArray();
        var newSaves = awaiting.Where(job => transfers.State.AnnouncedIncoming.Add(IncomingKey(job)!)).ToArray();
        var newCount = newInvites.Length + newSaves.Length;
        if (newCount == 0) return;
        try { transfers.SaveSettings(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            foreach (var offer in newInvites) transfers.State.AnnouncedIncoming.Remove(IncomingKey(offer)!);
            foreach (var job in newSaves) transfers.State.AnnouncedIncoming.Remove(IncomingKey(job)!);
            return;
        }
        // Group a batch so Windows does not replace one balloon per incoming item. No names or paths on screen.
        balloonTarget = newCount == 1 ? IncomingKey(newInvites.Length == 1 ? newInvites[0] : newSaves[0]) : null;
        var title = newCount != 1 ? "有收件待處理" : newInvites.Length == 1 ? "收到傳送邀請"
            : newSaves[0].IsAwaitingFirstSave ? "檔案接收完成" : "有收件待重試保存";
        tray.ShowBalloonTip(5000, title, $"{newCount} 筆收件待處理。點擊前往接收頁。", Forms.ToolTipIcon.Info);
    }
    private void OpenReceiving(string? target = null)
    {
        if (exiting) return;
        Show(); WindowState = WindowState.Normal; Activate();
        Tabs.SelectedItem = ReceiveTab;
        var items = Incoming.Items.Cast<object>();
        var match = target == null ? null : items.FirstOrDefault(item => IncomingKey(item) == target);
        match ??= items.FirstOrDefault(item => item is IncomingOffer or TransferJob { CanSave: true });
        if (match == null) return;
        Incoming.SelectedItem = match;
        Incoming.ScrollIntoView(match);
        Incoming.Focus();
    }
    private void GoToNextIncoming(object sender, RoutedEventArgs e)
    {
        var actionable = Incoming.Items.Cast<object>().Where(item => item is IncomingOffer or TransferJob { CanSave: true }).ToArray();
        if (actionable.Length == 0) return;
        var current = Array.IndexOf(actionable, Incoming.SelectedItem);
        OpenReceiving(IncomingKey(actionable[(current + 1) % actionable.Length]));
    }
    private void ViewIncoming(object sender, RoutedEventArgs e)
    {
        if (History.SelectedItem is not TransferJob { Kind: "receive" or "watch-receive" } job) return;
        var key = IncomingKey(job);
        if (!Incoming.Items.Cast<object>().Any(item => IncomingKey(item) == key))
        {
            SetNotice("暫時無法定位這筆收件。請等候狀態更新後再試。", HistoryTab);
            return;
        }
        OpenReceiving(key);
    }
    private void ChangeReceiveNotifications(object sender, RoutedEventArgs e)
    {
        if (!TrySavePreference(ReceiveNotifications, transfers.State.ReceiveNotifications, transfers.SetReceiveNotifications)) return;
        SetNotice(transfers.State.ReceiveNotifications ? "已開啟收件通知。" : "已關閉收件通知。待處理項目仍顯示在接收頁。", SettingsTab);
    }
    private void ChangeKeepBackground(object sender, RoutedEventArgs e)
    {
        if (!TrySavePreference(KeepBackground, transfers.State.KeepBackground, transfers.SetKeepBackground)) return;
        SetNotice(transfers.State.KeepBackground ? "關閉視窗後，NearDrop 會留在系統匣繼續傳輸。" : "關閉視窗後，NearDrop 會退出並停止傳輸。", SettingsTab);
    }
    private bool TrySavePreference(CheckBox control, bool previous, Action<bool> save)
    {
        if (restoringPreference) return false;
        try { save(control.IsChecked == true); return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            restoringPreference = true;
            try { control.IsChecked = previous; }
            finally { restoringPreference = false; }
            SetNotice("無法儲存設定，仍使用原本的設定。\n" + e.Message, SettingsTab);
            return false;
        }
    }
    private void CopyId(object sender, RoutedEventArgs e)
    {
        if (engine.DeviceId.Length == 0) return;
        try { Clipboard.SetText(engine.DeviceId); SetNotice("已複製完整裝置 ID。", DeviceTab); }
        catch (System.Runtime.InteropServices.ExternalException)
        { SetNotice("無法寫入剪貼簿。可選取裝置 ID 後手動複製。", DeviceTab); }
    }
    private async void AddFriend(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var added = await pairing.AddAsync(FriendId.Text, FriendName.Text, Verified.IsChecked == true);
        FriendId.Clear(); FriendName.Clear(); Verified.IsChecked = false;
        SetNotice(added ? "已在本機授權這台裝置。雙方都加入後才能建立連線。" : "已儲存這台裝置的名稱。", DeviceTab);
        await RefreshAsync();
    });
    private async void RemoveFriend(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (Friends.SelectedItem is not Friend friend) return;
        if (MessageBox.Show($"解除與 {friend.Name} 的配對及全部共享權限？已保存的檔案會保留。", "解除配對", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        await pairing.RemoveAsync(friend.DeviceId); SetNotice("已撤銷共享權限。"); await RefreshAsync();
    });
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (exiting) return;
        e.Cancel = true;
        if (KeepBackground.IsChecked == true) Hide();
        else _ = ExitAsync();
    }
    private async void ExitApp(object sender, RoutedEventArgs e) => await ExitAsync();
    private async Task ExitAsync()
    {
        if (exiting) return;
        exiting = true; timer.Stop(); IsEnabled = false;
        if (!publishing) preparation?.Cancel();
        await actions.WaitAsync();
        while (refreshing) await Task.Delay(50);
        if (source != null && hotkey) ExplorerIntegration.UnregisterHotKey(source.Handle, 1);
        await engine.DisposeAsync(); tray?.Dispose(); Close(); Application.Current.Shutdown();
    }
    public void ReceiveSelection(string[] paths)
    {
        if (paths.Length > 0)
        {
            Tabs.SelectedItem = SendTab;
            if (preparation != null)
                SetNotice("目前正在準備傳送。請等候完成，或取消後再加入檔案。", SendTab);
            else
                AppendDraft(paths);
        }
        Show(); WindowState = WindowState.Normal; Activate();
    }
    private void AppendDraft(string[] paths)
    {
        var existing = new HashSet<string>(sendSelection.Select(item => item.FullPath), StringComparer.OrdinalIgnoreCase);
        var added = 0;
        var unavailable = 0;
        foreach (var path in paths)
        {
            try
            {
                var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                if (Path.GetPathRoot(full) == full) { unavailable++; continue; }
                var folder = Directory.Exists(full);
                var file = new FileInfo(full);
                if (!folder && !file.Exists) { unavailable++; continue; }
                if (!existing.Add(full)) continue;
                sendSelection.Add(new(full, folder, folder ? 0 : file.Length));
                added++;
            }
            catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
            { unavailable++; }
        }
        SetNotice(unavailable > 0 ? $"有 {unavailable} 個項目無法加入。已加入 {added} 個，原有清單保留。"
            : added == 0 ? "選取的項目已在清單中。" : "", SendTab);
        UpdateDraftSummary();
    }
    private void UpdateDraftSummary()
    {
        var folders = sendSelection.Count(item => item.IsFolder);
        var bytes = sendSelection.Sum(item => item.Length);
        SelectionSummary.Text = sendSelection.Count == 0 ? "尚未選取檔案" :
            $"{sendSelection.Count} 個項目 · {(folders == 0 ? TransferJob.Format(bytes) : $"含 {folders} 個資料夾，容量於準備時計算")}";
        UpdateActions();
    }
    private void RemoveDraftItem(object sender, RoutedEventArgs e)
    {
        if (preparation != null || DraftItems.SelectedItem is not SendSelection item) return;
        sendSelection.Remove(item);
        UpdateDraftSummary();
    }
    private void ClearDraft(object sender, RoutedEventArgs e)
    {
        if (preparation != null) return;
        sendSelection.Clear();
        UpdateDraftSummary();
    }
    private sealed record SendSelection(string FullPath, bool IsFolder, long Length)
    {
        public string Display => $"{(IsFolder ? "資料夾" : "檔案")} · {Path.GetFileName(FullPath)}{(IsFolder ? "" : " · " + TransferJob.Format(Length))}";
    }
    private void FilesDropped(object s, DragEventArgs e) { if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) ReceiveSelection(paths); }
    private void FilesDragged(object s, DragEventArgs e) { e.Effects = preparation == null && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private void PickFiles(object s, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "選取要傳送的檔案" };
        if (dialog.ShowDialog(this) == true) ReceiveSelection(dialog.FileNames);
    }
    private string? ChooseFolder(string title)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title, Multiselect = false };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }
    private void PickFolder(object s, RoutedEventArgs e) { var path = ChooseFolder("選取要傳送的資料夾"); if (path != null) ReceiveSelection([path]); }
    private async void SendFiles(object s, RoutedEventArgs e) => await Run(async () =>
    {
        if (!ready) throw new InvalidOperationException("引擎尚未就緒。");
        if (SendFriends.SelectedItem is not Friend friend) throw new InvalidOperationException("請先選取收件朋友。");
        var paths = sendSelection.Select(item => item.FullPath).ToArray();
        if (paths.Length == 0) return;
        using var cancellation = new CancellationTokenSource();
        preparation = cancellation;
        publishing = false;
        PreparationPanel.Visibility = Visibility.Visible;
        PreparationText.Text = "正在準備檔案…";
        PreparationBar.IsIndeterminate = true;
        CancelPreparationButton.IsEnabled = true;
        UpdateActions();
        var progress = new Progress<SnapshotProgress>(value =>
        {
            if (preparation != cancellation || publishing || cancellation.IsCancellationRequested) return;
            PreparationText.Text = $"已複製 {value.FilesCopied} 個檔案 · {TransferJob.Format(value.BytesCopied)}" +
                (value.FileName.Length == 0 ? " · 正在驗證暫存清單…" : "\n目前檔案：" + value.FileName);
            PreparationBar.IsIndeterminate = value.FileLength <= 0;
            if (value.FileLength > 0) PreparationBar.Value = Math.Clamp(100d * value.FileBytesCopied / value.FileLength, 0, 100);
        });
        TransferJob sent;
        try
        {
            sent = await transfers.SendAsync(paths, friend, cancellation.Token, progress, () =>
            {
                publishing = true;
                CancelPreparationButton.IsEnabled = false;
                PreparationBar.IsIndeterminate = true;
                PreparationText.Text = "暫存已完成，正在建立傳送邀請…";
            });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            SetNotice("已取消準備，沒有建立傳送邀請。選取清單仍保留。", SendTab);
            return;
        }
        catch (Exception error)
        {
            SetNotice(error.Message, SendTab);
            return;
        }
        finally
        {
            preparation = null;
            publishing = false;
            PreparationPanel.Visibility = Visibility.Collapsed;
            UpdateActions();
        }
        sendSelection.Clear();
        UpdateDraftSummary();
        SetNotice("", SendTab);
        await RefreshAsync();
        const string sentNotice = "本機已建立傳送共享。對方接受後可開始同步。";
        if (Tabs.SelectedItem == SendTab)
        {
            UpdateHistory();
            var hiddenByFilters = !History.Items.Contains(sent);
            if (hiddenByFilters) ClearHistoryFilters();
            Tabs.SelectedItem = HistoryTab;
            History.SelectedItem = sent;
            History.ScrollIntoView(sent);
            SetNotice(hiddenByFilters ? sentNotice + " 已清除原篩選以顯示這筆工作。" : sentNotice, HistoryTab);
        }
        else SetNotice(sentNotice, HistoryTab);
    }, disableWindow: false);
    private void CancelPreparation(object sender, RoutedEventArgs e)
    {
        if (preparation == null || publishing) return;
        preparation.Cancel();
        CancelPreparationButton.IsEnabled = false;
        PreparationBar.IsIndeterminate = true;
        PreparationText.Text = "正在取消並清理暫存…";
    }
    private async void AcceptIncoming(object s, RoutedEventArgs e) => await Run(async () =>
    {
        if (Incoming.SelectedItem is not IncomingOffer offer) throw new InvalidOperationException("請選取等待接受的傳送。");
        if (offer.Continuous && MessageBox.Show("這是持續同步資料夾；朋友之後的修改與刪除會影響這份接收副本。若需永久保存，請另外複製到不共享的位置。是否接受？", "接受持續資料夾", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        try
        {
            await transfers.AcceptAsync(offer);
            SetNotice("已接受邀請。接收進度顯示在工作列。");
        }
        finally
        {
            await RefreshAsync();
            if (Incoming.SelectedItem is TransferJob selected && IncomingSelectionId(selected) == IncomingSelectionId(offer))
                Incoming.ScrollIntoView(selected);
        }
    });
    private async void DeclineIncoming(object s, RoutedEventArgs e) => await Run(async () =>
    {
        if (Incoming.SelectedItem is not IncomingOffer offer) throw new InvalidOperationException("請選取等待接受的邀請。");
        await transfers.DeclineAsync(offer);
        SetNotice("已拒絕這次邀請。若要收件，請朋友重新發送新的邀請。");
        await RefreshAsync();
    });
    private async void SaveIncoming(object s, RoutedEventArgs e) => await Run(async () =>
    {
        if (Incoming.SelectedItem is not TransferJob { CanSave: true } job) throw new InvalidOperationException("請選取已接收、待保存的檔案。");
        SetNotice("正在隔離並驗證內容；其他傳輸會短暫暫停…");
        try
        {
            await transfers.SaveReceivedAsync(job);
            SetNotice("已獨立保存。朋友無法再透過共享修改或刪除這份檔案。");
        }
        catch (EngineRestoreException error) when (job.Saved)
        {
            SetNotice("檔案已獨立保存。\n" + error.Message);
        }
        finally
        {
            await RefreshAsync();
        }
    });
    private void OpenInbox(object s, RoutedEventArgs e)
    {
        OpenFolderFromPage(transfers.State.Inbox, ReceiveTab, "找不到單次收件的保存資料夾。請在設定變更保存位置。");
    }
    private void OpenReceived(object s, RoutedEventArgs e)
    {
        if (Incoming.SelectedItem is not TransferJob { Saved: true, SavedPath: string path }) return;
        OpenFolderFromPage(SavedFilesPath(path), ReceiveTab, "找不到這筆收件的檔案資料夾。它可能已搬移或刪除。");
    }
    private static string SavedFilesPath(string path) => Path.Combine(path, "Files");
    private void OpenReceivedWatch(object s, RoutedEventArgs e)
    {
        if (Incoming.SelectedItem is TransferJob { Kind: "watch-receive" } job)
            OpenFolderFromPage(job.Path, ReceiveTab, "找不到同步資料夾。它可能尚未建立、已搬移或已刪除。");
    }
    private void OpenWatch(object s, RoutedEventArgs e)
    {
        if (Watches.SelectedItem is TransferJob { Kind: "watch-send" or "watch-receive" } job)
            OpenFolderFromPage(job.Path, WatchTab, "找不到同步資料夾。它可能尚未建立、已搬移或已刪除。");
    }
    private void OpenFolderFromPage(string path, TabItem page, string missingMessage)
    {
        if (!Directory.Exists(path)) { SetNotice(missingMessage, page); return; }
        try { OpenFolder(path); SetNotice("", page); }
        catch (Exception e) when (e is Win32Exception or IOException)
        { SetNotice("無法開啟資料夾。可從畫面複製路徑後，在檔案總管試開。", page); }
    }
    private static void OpenFolder(string path)
    {
        var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false }; info.ArgumentList.Add(Path.GetFullPath(path)); Process.Start(info);
    }
    private async void AddWatch(object s, RoutedEventArgs e)
    {
        var dialog = new FriendPicker(SendFriends.Items.Cast<Friend>().ToArray()) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Selected == null) return;
        var path = ChooseFolder("選取專用的自動傳送資料夾"); if (path == null) return;
        if (MessageBox.Show($"持續傳給 {dialog.Selected.Name}：\n{path}\n\n新增、修改及刪除都會同步，這不是備份。對方不能反向修改你的來源。是否啟用？", "新增固定資料夾", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        await Run(async () =>
        {
            var job = await transfers.WatchAsync(path, dialog.Selected);
            SetNotice("已啟用固定資料夾，等待朋友接受。", WatchTab);
            await RefreshAsync();
            if (Tabs.SelectedItem == WatchTab && Watches.Items.Contains(job))
            {
                Watches.SelectedItem = job;
                Watches.ScrollIntoView(job);
            }
        });
    }
    private async void ToggleWatch(object s, RoutedEventArgs e) => await Run(async () =>
    {
        if (Watches.SelectedItem is not TransferJob job) return;
        if (CanReenableWatch(job))
        {
            await transfers.RetryAsync(job);
            SetNotice("本機已重新啟用共享。雙方共享此資料夾時即可同步。", WatchTab);
        }
        else await transfers.ToggleAsync(job);
        await RefreshAsync();
    });
    private async void StopWatch(object s, RoutedEventArgs e) => await StopSelectedAsync(Watches);
    private async void RetryTransfer(object s, RoutedEventArgs e) => await Run(async () =>
    {
        if (History.SelectedItem is not TransferJob job) return;
        if (job is { Paused: true, CanToggleWatch: true }) await transfers.ToggleAsync(job);
        else await transfers.RetryAsync(job);
        await RefreshAsync();
    });
    private async void CancelTransfer(object s, RoutedEventArgs e) => await StopSelectedAsync(History);
    private Task StopSelectedAsync(ListBox list) => Run(async () =>
    {
        if (list.SelectedItem is not TransferJob { CanStop: true } job) return;
        var continuous = job.Kind.StartsWith("watch", StringComparison.Ordinal);
        var title = StopLabel(job);
        var effect = continuous ? "停止後，新的變更不會繼續同步。已在本機的檔案會保留。"
            : job.CanSave ? "停止共享後，這份已完整接收的檔案仍可獨立保存。"
            : job.Kind == "receive" ? "這筆收件會停止，之後無法使用獨立保存。已在本機的檔案會保留。"
            : "如果對方還在接收，這次傳送會中斷。雙方目前已有的本機檔案會保留。";
        if (MessageBox.Show(this, $"{job.Label}\n對方：{job.FriendName}\n\n{effect}\n\n確定要停止？",
            title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await transfers.CancelAsync(job);
        SetNotice(continuous ? "已停止持續同步。" : job.Kind == "send" ? "已停止這次傳送。本機暫存仍保留。"
            : job.IsAwaitingFirstSave ? "已停止共享。仍可到接收頁獨立保存。"
            : "已停止這次接收。本機已有的檔案仍保留。");
        await RefreshAsync();
    });
    private async void OpenOutbox(object s, RoutedEventArgs e) => await Run(() =>
    {
        if (History.SelectedItem is not TransferJob job) throw new InvalidOperationException("請選取一筆單次傳送工作。");
        var path = transfers.GetOutboxPath(job);
        if (!Directory.Exists(path)) throw new IOException("這筆工作的暫存資料夾已不存在。");
        OpenFolder(path);
        return Task.CompletedTask;
    });
    private async void ClearOutbox(object s, RoutedEventArgs e) => await Run(async () =>
    {
        if (History.SelectedItem is not TransferJob job) throw new InvalidOperationException("請選取一筆單次傳送工作。");
        transfers.GetOutboxPath(job);
        if (job.OutboxRemoved) { SetNotice("這筆工作的暫存已清理。"); return; }
        if (!job.Detached) throw new InvalidOperationException("請先確認朋友已保存，再取消共享。取消後才可刪除暫存。");
        var confirmed = MessageBox.Show(this,
            "建議先請朋友確認已獨立保存。清理會短暫暫停其他傳輸，永久刪除這筆工作的本機暫存。要再傳送時需重新選取原始檔案。確定清理？",
            "清理傳送暫存", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirmed != MessageBoxResult.Yes) return;
        try
        {
            await transfers.ClearOutboxAsync(job);
            SetNotice("已清理這筆工作的傳送暫存。");
        }
        catch (EngineRestoreException error) when (job.OutboxRemoved)
        {
            SetNotice("這筆工作的暫存已清理。\n" + error.Message);
        }
        finally
        {
            await RefreshAsync();
        }
    });
    private void ChangeInbox(object s, RoutedEventArgs e)
    {
        var path = ChooseFolder("選取未來獨立保存的位置（本機 NTFS）"); if (path == null) return;
        try { transfers.SetInbox(path); InboxPath.Text = transfers.State.Inbox; SetNotice("已更新單次收件的保存位置。之前保存的檔案仍在原資料夾。"); }
        catch (Exception error) { SetNotice(error.Message); }
    }
    private void ChangeStartup(object s, RoutedEventArgs e)
    {
        try
        {
            var enabled = StartAtLogin.IsChecked == true;
            ExplorerIntegration.SetStartup(enabled);
            SetNotice(enabled ? "已將此版本設為登入 Windows 時背景啟動。" : "已停用此版本的登入啟動。");
        }
        catch (Exception error) { StartAtLogin.IsChecked = ExplorerIntegration.StartupEnabled; SetNotice(error.Message); }
    }
    private void OpenDocs(object s, RoutedEventArgs e) => OpenFolder(Path.Combine(AppContext.BaseDirectory, "docs"));
    private void InstallSendTo(object s, RoutedEventArgs e)
    {
        try { ExplorerIntegration.InstallSendTo(); SetNotice("已加入右鍵 → 顯示其他選項 → 傳送到 → NearDrop。"); }
        catch (Exception error) { SetNotice(error.Message); }
    }
    private void EnableHotkey(object s, RoutedEventArgs e)
    {
        if (!TrySavePreference(ExplorerHotkey, transfers.State.ExplorerHotkey, transfers.SetExplorerHotkey)) return;
        ApplyHotkey(transfers.State.ExplorerHotkey);
    }
    private void ApplyHotkey(bool enabled)
    {
        if (source == null) return;
        if (!enabled)
        {
            if (hotkey && !ExplorerIntegration.UnregisterHotKey(source.Handle, 1))
            {
                var unregisterError = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                SetNotice($"已儲存停用偏好，但 Windows 未能停用目前的快捷鍵（錯誤 {unregisterError}）。重新開啟 NearDrop 後生效。", SettingsTab);
                return;
            }
            hotkey = false;
            SetNotice("已停用快捷鍵。", SettingsTab);
            return;
        }
        if (hotkey) return;
        hotkey = ExplorerIntegration.RegisterHotKey(source.Handle, 1, 0x4003, 0x4E);
        var error = hotkey ? 0 : System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        SetNotice(hotkey ? "Ctrl+Alt+N 已啟用。在檔案總管選取項目後按下，即可帶入傳送清單。"
            : error == 1409 ? "Ctrl+Alt+N 已被其他程式使用。仍可拖曳檔案或使用右鍵傳送。"
            : $"Windows 未能註冊 Ctrl+Alt+N（錯誤 {error}）。仍可拖曳檔案或使用右鍵傳送。", SettingsTab);
    }
    private IntPtr WindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0312 && wParam.ToInt32() == 1)
        {
            handled = true;
            if (exiting) return IntPtr.Zero;
            try
            {
                var paths = ExplorerIntegration.SelectedExplorerFiles();
                if (paths.Length > 0) ReceiveSelection(paths);
                else ShowShortcutError("請先在檔案總管選取要傳送的項目。若無法帶入所選項目，請改用拖曳。");
            }
            catch (Exception error) { ShowShortcutError("無法從檔案總管帶入項目。\n" + error.Message); }
        }
        return IntPtr.Zero;
    }
    private void ShowShortcutError(string message)
    {
        Tabs.SelectedItem = SendTab;
        SetNotice(message, SendTab);
        ReceiveSelection([]);
    }
}
