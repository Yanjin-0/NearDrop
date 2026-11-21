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
        Check(history.Items.Count == 1 && ((TransferJob)history.Items[0]).Id == "old", "已接收／已保存篩選包含同步完成但保留未知保存狀態");
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
            new() { Id = "sync", Label = "監控資料夾", Kind = "watch-receive", State = "目前已同步 · 持續監控", Created = date }
        ];
    }
}
