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
    private readonly ObservableCollection<SendSelection> sendSelection = [];
    private CancellationTokenSource? preparation;
    private bool publishing;
    private HwndSource? source;
    private bool hotkey;
    public MainWindow(string profile, bool localTest)
    {
        InitializeComponent(); this.profile = profile;
        engine = new SyncthingEngine(Path.Combine(AppContext.BaseDirectory, "engine", "syncthing.exe"), Path.Combine(profile, "engine"), localTest);
        pairing = new PairingService(engine);
        transfers = new TransferService(engine, profile);
        KeepBackground.IsChecked = transfers.State.KeepBackground;
        KeepBackground.Click += (_, _) => { transfers.State.KeepBackground = KeepBackground.IsChecked == true; transfers.SaveSettings(); };
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
            ready = true; EngineStatus.Text = "安全傳輸已啟動";
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
        if (!IsInitialized || ClearOutboxButton == null || RemoveFriendButton == null) return;
        var haveFriends = SendFriends.Items.Count > 0;
        NoFriends.Visibility = ready && !haveFriends ? Visibility.Visible : Visibility.Collapsed;
        var preparing = preparation != null;
        SendButton.IsEnabled = ready && !preparing && sendSelection.Count > 0 && SendFriends.SelectedItem is Friend;
        RemoveDraftButton.IsEnabled = !preparing && DraftItems.SelectedItem is SendSelection;
        ClearDraftButton.IsEnabled = !preparing && sendSelection.Count > 0;
        PickFilesButton.IsEnabled = PickFolderButton.IsEnabled = !preparing;
        DraftPanel.Visibility = sendSelection.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SendHint.Text = preparing ? "你可以切換頁面查看其他工作。"
            : !ready ? "傳輸引擎尚未就緒。" : sendSelection.Count == 0 ? "先選取檔案或資料夾。"
            : !haveFriends ? "先新增一位朋友。已選取的檔案會保留。"
            : SendFriends.SelectedItem is not Friend ? "選擇一位收件人。" : "傳送會建立暫存副本，約需一份額外磁碟空間。";

        var offer = Incoming.SelectedItem as IncomingOffer;
        var received = Incoming.SelectedItem as TransferJob;
        SetAction(AcceptButton, ready && offer != null);
        SetAction(DeclineButton, ready && offer != null);
        SetAction(SaveButton, ready && received?.CanSave == true);
        SetAction(OpenReceivedButton, received?.Saved == true);
        IncomingHint.Text = offer != null ? (offer.Continuous ? "接受後，來源的修改與刪除會同步。" : "接受這次邀請後即可接收檔案。")
            : received?.Saved == true ? "已保存。可以開啟檔案所在資料夾。"
            : received?.CanSave == true ? "接收完成。保存後，這份檔案會脫離共享。"
            : received != null ? "接收狀態顯示在選取的工作上。" : "選取一筆邀請或收件查看可用操作。";

        var watch = Watches.SelectedItem as TransferJob;
        AddWatchButton.IsEnabled = ready && haveFriends;
        ToggleWatchButton.IsEnabled = StopWatchButton.IsEnabled = ready && CanStop(watch);
        ToggleWatchButton.Content = watch?.Paused == true ? "恢復" : "暫停";
        var job = History.SelectedItem as TransferJob;
        StopTransferButton.IsEnabled = ready && CanStop(job);
        StopTransferButton.Content = job == null ? "停止這次傳送" : StopLabel(job);
        RetryButton.IsEnabled = ready && job is { Saved: false, CleanupStarted: false } && !(job.Detached && job.Kind == "receive");
        OpenOutboxButton.IsEnabled = job is { Kind: "send", OutboxRemoved: false };
        ClearOutboxButton.IsEnabled = ready && job is { Kind: "send", Detached: true, OutboxRemoved: false };
        AddFriendButton.IsEnabled = ready && Verified.IsChecked == true && !string.IsNullOrWhiteSpace(FriendName.Text) && !string.IsNullOrWhiteSpace(FriendId.Text);
        RemoveFriendButton.IsEnabled = ready && Friends.SelectedItem is Friend;
    }
    private static bool CanStop(TransferJob? job) => job is { Saved: false, Detached: false };
    private static string StopLabel(TransferJob job) => job.Kind switch
    {
        "watch-send" or "watch-receive" => "停止持續同步",
        "receive" => "停止這次接收",
        _ => "停止這次傳送"
    };
    private static void SetAction(Button button, bool available)
    {
        button.IsEnabled = available;
        button.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
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
            var incomingId = IncomingKey(Incoming.SelectedItem);
            var watchId = (Watches.SelectedItem as TransferJob)?.Id;
            var received = transfers.State.Jobs.Where(j => j.Kind.EndsWith("receive", StringComparison.Ordinal))
                .OrderByDescending(j => j.CanSave).ThenByDescending(j => j.Created).ToArray();
            var incoming = transfers.Offers.Cast<object>().Concat(received).ToArray();
            Incoming.ItemsSource = incoming;
            Incoming.SelectedItem = incomingId == null ? null : incoming.FirstOrDefault(i => IncomingKey(i) == incomingId);
            var jobs = transfers.State.Jobs.OrderByDescending(j => j.Created).ToArray();
            UpdateHistory();
            Watches.ItemsSource = jobs.Where(j => j.Kind.StartsWith("watch")).ToArray(); Watches.SelectedItem = jobs.FirstOrDefault(j => j.Id == watchId);
            UpdateReceivingAttention(received);
            EngineStatus.Text = friends.Count == 0 ? "安全傳輸已啟動" : $"{friends.Count(f => f.Connected)} 位朋友在線上";
        }
        catch (Exception) { EngineStatus.Text = "暫時無法更新傳輸狀態"; }
        finally { refreshing = false; UpdateActions(); }
    }
    private void HistoryFilterChanged(object sender, RoutedEventArgs e) => UpdateHistory();
    private void ResetHistoryFilters(object sender, RoutedEventArgs e)
    {
        HistorySearch.Clear();
        HistoryStatusFilter.SelectedIndex = 0;
        HistoryDirectionFilter.SelectedIndex = 0;
        HistorySearch.Focus();
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
    private void UpdateReceivingAttention(TransferJob[] received)
    {
        var invites = transfers.Offers;
        var awaiting = received.Where(job => job.CanSave).ToArray();
        var count = invites.Count + awaiting.Length;
        ReceiveTab.Header = count == 0 ? "接收" : $"接收（{count}）";
        IncomingSummary.Text = count == 0 ? "目前沒有待接受或待保存的收件。"
            : $"{invites.Count} 筆待接受 · {awaiting.Length} 筆待保存";
        NextIncomingButton.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (receiveMenu != null) receiveMenu.Text = count == 0 ? "開啟接收頁" : $"開啟接收頁（{count} 筆待處理）";
        if (!transfers.State.ReceiveNotifications || tray == null) return;

        var newInvites = invites.Where(offer => transfers.State.AnnouncedIncoming.Add(IncomingKey(offer)!)).ToArray();
        var newSaves = awaiting.Where(job => transfers.State.AnnouncedIncoming.Add(IncomingKey(job)!)).ToArray();
        var newCount = newInvites.Length + newSaves.Length;
        if (newCount == 0) return;
        transfers.SaveSettings();
        // Group a batch so Windows does not replace one balloon per incoming item. No names or paths on screen.
        balloonTarget = newCount == 1 ? IncomingKey(newInvites.Length == 1 ? newInvites[0] : newSaves[0]) : null;
        var title = newCount == 1 ? newInvites.Length == 1 ? "收到檔案邀請" : "檔案接收完成" : "有收件待處理";
        tray.ShowBalloonTip(5000, title, $"{newInvites.Length} 筆新邀請、{newSaves.Length} 筆新待保存。點擊前往接收頁。", Forms.ToolTipIcon.Info);
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
    private void ChangeReceiveNotifications(object sender, RoutedEventArgs e)
    {
        transfers.State.ReceiveNotifications = ReceiveNotifications.IsChecked == true;
        transfers.SaveSettings();
        SetNotice(transfers.State.ReceiveNotifications ? "已開啟收件通知。" : "已關閉收件通知。待處理項目仍顯示在接收頁。", SettingsTab);
    }
    private void CopyId(object sender, RoutedEventArgs e) { if (ready) { Clipboard.SetText(engine.DeviceId); SetNotice("已複製完整裝置 ID。"); } }
    private async void AddFriend(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        await pairing.AddAsync(FriendId.Text, FriendName.Text, Verified.IsChecked == true);
        FriendId.Clear(); FriendName.Clear(); Verified.IsChecked = false;
        SetNotice("已授權此朋友。請對方也核對並加入你的完整 ID。"); await RefreshAsync();
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
        try
        {
            await transfers.SendAsync(paths, friend, cancellation.Token, progress, () =>
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
        SetNotice("已排入傳送。等待朋友接受邀請。", HistoryTab);
        if (Tabs.SelectedItem == SendTab) Tabs.SelectedItem = HistoryTab;
        await RefreshAsync();
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
        await transfers.AcceptAsync(offer); SetNotice("已接受，正在接收。"); await RefreshAsync();
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
        await transfers.SaveReceivedAsync(job); SetNotice("已獨立保存。朋友無法再透過共享修改或刪除這份檔案。"); await RefreshAsync();
    });
    private void OpenInbox(object s, RoutedEventArgs e)
    {
        var path = transfers.State.Inbox;
        if (Directory.Exists(path)) OpenFolder(path);
        else SetNotice("找不到收件資料夾。請在設定重新選擇收件位置。", ReceiveTab);
    }
    private void OpenReceived(object s, RoutedEventArgs e)
    {
        if (Incoming.SelectedItem is not TransferJob { Saved: true, SavedPath: string path }) return;
        if (Directory.Exists(path)) OpenFolder(path);
        else SetNotice("找不到已保存的位置。資料夾可能已搬移或刪除。", ReceiveTab);
    }
    private static void OpenFolder(string path)
    {
        var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false }; info.ArgumentList.Add(Path.GetFullPath(path)); Process.Start(info);
    }
    private async void AddWatch(object s, RoutedEventArgs e)
    {
        var friends = await pairing.ListAsync();
        var dialog = new FriendPicker(friends) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Selected == null) return;
        var path = ChooseFolder("選取專用的自動傳送資料夾"); if (path == null) return;
        if (MessageBox.Show($"持續傳給 {dialog.Selected.Name}：\n{path}\n\n新增、修改及刪除都會同步，這不是備份。對方不能反向修改你的來源。是否啟用？", "新增固定資料夾", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        await Run(async () => { await transfers.WatchAsync(path, dialog.Selected); SetNotice("已啟用固定資料夾，等待朋友接受。"); await RefreshAsync(); });
    }
    private async void ToggleWatch(object s, RoutedEventArgs e) => await Run(async () => { if (Watches.SelectedItem is TransferJob job) await transfers.ToggleAsync(job); await RefreshAsync(); });
    private async void StopWatch(object s, RoutedEventArgs e) => await StopSelectedAsync(Watches);
    private async void RetryTransfer(object s, RoutedEventArgs e) => await Run(async () => { if (History.SelectedItem is TransferJob job) await transfers.RetryAsync(job); await RefreshAsync(); });
    private async void CancelTransfer(object s, RoutedEventArgs e) => await StopSelectedAsync(History);
    private Task StopSelectedAsync(ListBox list) => Run(async () =>
    {
        if (list.SelectedItem is not TransferJob job || !CanStop(job)) return;
        var continuous = job.Kind.StartsWith("watch", StringComparison.Ordinal);
        var title = StopLabel(job);
        var effect = continuous ? "停止後，新的變更不會繼續同步。已在本機的檔案會保留。"
            : job.Kind == "receive" ? "如果還沒接收完成，這次接收會中斷。已在本機的檔案會保留。"
            : "如果對方還在接收，這次傳送會中斷。雙方目前已有的本機檔案會保留。";
        if (MessageBox.Show(this, $"{job.Label}\n對方：{job.FriendName}\n\n{effect}\n\n確定要停止？",
            title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await transfers.CancelAsync(job);
        SetNotice(continuous ? "已停止持續同步。" : job.Kind == "send" ? "已停止這次傳送。本機暫存仍保留。" : "已停止這次接收。本機已有的檔案仍保留。");
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
        await transfers.ClearOutboxAsync(job);
        SetNotice("已清理這筆工作的傳送暫存。");
        await RefreshAsync();
    });
    private void ChangeInbox(object s, RoutedEventArgs e)
    {
        var path = ChooseFolder("選取未來獨立保存的位置（本機 NTFS）"); if (path == null) return;
        try { transfers.ValidateInbox(path); transfers.State.Inbox = path; transfers.SaveSettings(); InboxPath.Text = path; SetNotice("已變更新收件的位置，現有檔案不會搬移。"); }
        catch (Exception error) { SetNotice(error.Message); }
    }
    private void ChangeStartup(object s, RoutedEventArgs e)
    {
        try { ExplorerIntegration.SetStartup(StartAtLogin.IsChecked == true); SetNotice("已更新登入啟動設定。"); }
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
        transfers.State.ExplorerHotkey = ExplorerHotkey.IsChecked == true;
        transfers.SaveSettings();
        ApplyHotkey(transfers.State.ExplorerHotkey);
    }
    private void ApplyHotkey(bool enabled)
    {
        if (source == null) return;
        if (!enabled)
        {
            if (hotkey) ExplorerIntegration.UnregisterHotKey(source.Handle, 1);
            hotkey = false;
            SetNotice("已停用快捷鍵。", SettingsTab);
            return;
        }
        if (hotkey) return;
        hotkey = ExplorerIntegration.RegisterHotKey(source.Handle, 1, 0x4003, 0x4E);
        SetNotice(hotkey ? "已啟用 Ctrl+Alt+N：在檔案總管選取項目後按下，回到此處選朋友傳送。" : "Ctrl+Alt+N 已被其他程式使用，請使用拖曳或右鍵傳送。", SettingsTab);
    }
    private IntPtr WindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0312 && wParam.ToInt32() == 1)
        {
            handled = true;
            try
            {
                var paths = ExplorerIntegration.SelectedExplorerFiles();
                if (paths.Length > 0) ReceiveSelection(paths);
                else { SetNotice("請先在一般檔案總管視窗選取檔案。虛擬資料夾與部分分頁不支援，請改用拖曳。", SendTab); }
            }
            catch (Exception error) { SetNotice(error.Message, SendTab); }
        }
        return IntPtr.Zero;
    }
}
