using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Relay.App;
using Relay.Core;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var profile = Path.Combine(args[0], "test-results", "history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        var jobs = CreateJobs();
        new StateStore(Path.Combine(profile, "state.json")).Save(new() { Jobs = [..jobs] });
        var application = new App();
        application.InitializeComponent();
        // Instantiate the real WPF window, but never show it or create a native handle.
        var window = new MainWindow(profile, localTest: true);
        var search = Control<TextBox>(window, "HistorySearch");
        var status = Control<ComboBox>(window, "HistoryStatusFilter");
        var direction = Control<ComboBox>(window, "HistoryDirectionFilter");
        var history = Control<ListBox>(window, "History");
        var results = Control<TextBlock>(window, "HistoryResults");
        var reset = Control<Button>(window, "ResetHistoryButton");
        var stop = Control<Button>(window, "StopTransferButton");

        Check(history.Items.Count == jobs.Length && !reset.IsEnabled, "初始呈現全部工作且清除條件停用");
        search.Text = "alice 2026/11/18";
        Check(history.Items.Count == 3 && ((TransferJob)history.Items[0]).Id == "new", "不分大小寫搜尋朋友及日期，並依新到舊排列");
        direction.SelectedIndex = (int)HistoryDirection.Outgoing;
        Check(history.Items.Count == 2, "方向篩選與複合查詢可同時使用");
        status.SelectedIndex = (int)HistoryStatus.Delivered;
        Check(history.Items.Count == 1 && ((TransferJob)history.Items[0]).Id == "old", "已同步／已獨立保存篩選保留發送端的保存狀態未知");
        Check(((TransferJob)history.Items[0]).HistoryDisplay.Contains("保存狀態未知", StringComparison.Ordinal), "發送端仍清楚標明保存狀態未知");
        history.SelectedIndex = 0;
        search.Text = "Alice";
        Check(history.SelectedItem is TransferJob { Id: "old" }, "仍符合條件的選取可以保留");
        search.Text = "找不到的內容";
        Check(history.Items.Count == 0 && history.SelectedItem == null && !stop.IsEnabled && results.Text.Contains("沒有符合", StringComparison.Ordinal), "隱藏的工作無法操作，且顯示無結果提示");
        reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(history.Items.Count == jobs.Length && search.Text.Length == 0 && status.SelectedIndex == 0 && direction.SelectedIndex == 0,
            "清除按鈕同時重設搜尋、方向及狀態");
        status.SelectedIndex = (int)HistoryStatus.AwaitingSave;
        Check(history.Items.Count == 1 && ((TransferJob)history.Items[0]).CanSave, "待保存與已保存可分辨");
        Check(TransferHistory.StatusOf(jobs.Single(job => job.Id == "paused")) == HistoryStatus.Stopped &&
              TransferHistory.StatusOf(jobs.Single(job => job.Id == "failed")) == HistoryStatus.Attention &&
              TransferHistory.StatusOf(jobs.Single(job => job.Id == "sync")) == HistoryStatus.Active,
            "暫停、失敗和持續同步的狀態分類正確");
        var displayed = jobs[0];
        displayed.RefreshHistoryDisplay();
        var notifications = 0;
        displayed.PropertyChanged += (_, change) => { if (change.PropertyName == nameof(TransferJob.HistoryDisplay)) notifications++; };
        displayed.RefreshHistoryDisplay();
        displayed.DoneBytes += 40;
        displayed.RefreshHistoryDisplay();
        displayed.RefreshHistoryDisplay();
        Check(notifications == 1, "只在顯示內容有變動時更新既有清單列");

        var incoming = Control<ListBox>(window, "Incoming");
        var openReceivedWatch = Control<Button>(window, "OpenReceivedWatchButton");
        var openReceived = Control<Button>(window, "OpenReceivedButton");
        var localPath = Control<TextBox>(window, "IncomingLocalPath");
        incoming.ItemsSource = jobs.Where(job => job.Kind.EndsWith("receive", StringComparison.Ordinal));
        var synced = jobs.Single(job => job.Id == "sync");
        synced.Path = Path.Combine(profile, "Incoming", synced.Id);
        incoming.SelectedItem = synced;
        Check(openReceivedWatch.IsEnabled && openReceivedWatch.Visibility == Visibility.Visible && !openReceived.IsEnabled && localPath.Text == synced.Path,
            "持續同步收件可開啟其本機同步資料夾");
        var saved = jobs.Single(job => job.Id == "saved");
        saved.SavedPath = Path.Combine(profile, "Received", saved.Id);
        saved.TotalBytes = 329;
        incoming.SelectedItem = saved;
        Check(openReceivedWatch.Visibility == Visibility.Collapsed && openReceived.IsEnabled && localPath.Text == Path.Combine(saved.SavedPath, "Files"),
            "單次保存收件直接顯示檔案所在資料夾");
        Check(!saved.Display.Contains("329 B", StringComparison.Ordinal), "保存收件不把含清單的同步容量顯示為檔案大小");
        var pendingSave = jobs.Single(job => job.Id == "pending");
        pendingSave.SavedPath = Path.Combine(profile, "Received", pendingSave.Id);
        pendingSave.State = TransferJob.SaveFailed;
        pendingSave.Error = "測試保存錯誤";
        incoming.SelectedItem = pendingSave;
        Check((string)Control<Button>(window, "SaveButton").Content == "重試獨立保存" &&
            localPath.Text == pendingSave.SavedPath && TransferHistory.StatusOf(pendingSave) == HistoryStatus.Attention,
            "保存失敗顯示重試動作、原目的地與需要處理狀態");
        pendingSave.State = TransferJob.SaveRejected;
        incoming.SelectedItem = null;
        incoming.SelectedItem = pendingSave;
        Check(Control<Button>(window, "SaveButton").Visibility == Visibility.Collapsed &&
            Control<TextBlock>(window, "IncomingHint").Text.Contains("新的傳送邀請", StringComparison.Ordinal),
            "內容驗證拒絕時改提示重送");
        pendingSave.State = "已停止共享 · 本機檔案保留";
        pendingSave.SavedPath = null;
        pendingSave.Detached = true;
        incoming.SelectedItem = null;
        incoming.SelectedItem = pendingSave;
        Check(!pendingSave.CanSave && !pendingSave.CanRetry &&
            Control<TextBlock>(window, "IncomingHint").Text.Contains("新的傳送邀請", StringComparison.Ordinal),
            "停止的單次收件提示對方需建立新邀請");

        var watches = Control<ListBox>(window, "Watches");
        var openWatch = Control<Button>(window, "OpenWatchButton");
        watches.ItemsSource = jobs.Where(job => job.Kind.StartsWith("watch", StringComparison.Ordinal));
        Check(!openWatch.IsEnabled, "未選取持續同步工作時不開啟資料夾");
        watches.SelectedItem = jobs.Single(job => job.Id == "sync");
        Check(openWatch.IsEnabled, "可從自動傳送頁開啟收件同步資料夾");
        watches.SelectedItem = jobs.Single(job => job.Id == "paused");
        Check(openWatch.IsEnabled, "可開啟已暫停工作的本機同步資料夾");

        Check(!window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero, "測試全程沒有顯示視窗或建立原生視窗控制代碼");
        Console.WriteLine("Headless UI fixtures: " + profile);
    }

    private static T Control<T>(MainWindow window, string name) where T : FrameworkElement =>
        (T)(window.FindName(name) ?? throw new InvalidOperationException("Missing control: " + name));

    private static void Check(bool passed, string description)
    {
        if (!passed) throw new InvalidOperationException(description);
        Console.WriteLine("PASS " + description);
    }

    private static TransferJob[] CreateJobs()
    {
        var date = new DateTime(2026, 11, 18, 10, 0, 0);
        return
        [
            new() { Id = "new", Label = "活動影片", FriendName = "Alice", Kind = "send", State = "正在傳送", TotalBytes = 100, Created = date.AddHours(2) },
            new() { Id = "old", Label = "年度資料夾", FriendName = "Alice", Kind = "send", State = TransferJob.SenderSynced, Created = date },
            new() { Id = "pending", Label = "待保存相片", FriendName = "Alice", Kind = "receive", State = TransferJob.AwaitingSave, Created = date.AddHours(1) },
            new() { Id = "saved", Label = "已保存相片", FriendName = "Bob", Kind = "receive", Saved = true, Detached = true, Created = date },
            new() { Id = "paused", Label = "暫停資料夾", Kind = "watch-send", Paused = true, Created = date },
            new() { Id = "failed", Label = "清理未完成", Kind = "send", Error = "檔案鎖定", Detached = true, Created = date },
            new() { Id = "sync", Label = "監控資料夾", Kind = "watch-receive", State = "已同步至目前收到的版本 · 持續接收", Created = date }
        ];
    }
}
