using System.Text.Json;
using System.ComponentModel;

namespace Relay.Core;

public sealed class TransferJob : INotifyPropertyChanged
{
    private string? lastHistoryDisplay;
    public event PropertyChangedEventHandler? PropertyChanged;
    public const string AwaitingSave = "已接收 · 等待獨立保存";
    public const string SenderSynced = "對方已同步 · 保存狀態未知";
    public string Id { get; set; } = "";
    public string FriendId { get; set; } = "";
    public string FriendName { get; set; } = "";
    public string Label { get; set; } = "";
    public string Path { get; set; } = "";
    public string Kind { get; set; } = "send"; // send, receive, watch-send, watch-receive
    public string State { get; set; } = "準備中";
    public string? Error { get; set; }
    public bool Paused { get; set; }
    public bool Saved { get; set; }
    public bool Detached { get; set; }
    public bool CleanupStarted { get; set; }
    public bool OutboxRemoved { get; set; }
    public string? SavedPath { get; set; }
    public long TotalBytes { get; set; }
    public long DoneBytes { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public double RecentBytesPerSecond { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public DateTime LastProgressAt { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;
    [System.Text.Json.Serialization.JsonIgnore]
    public string HistoryDisplay => $"{TransferHistory.DirectionLabel(this)} · {Created:yyyy-MM-dd HH:mm}\n{Display}";
    public void RefreshHistoryDisplay()
    {
        var current = HistoryDisplay;
        if (lastHistoryDisplay == current) return;
        lastHistoryDisplay = current;
        PropertyChanged?.Invoke(this, new(nameof(HistoryDisplay)));
    }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool CanSave => Kind == "receive" && !Saved && (State == AwaitingSave || SavedPath != null);
    [System.Text.Json.Serialization.JsonIgnore]
    public string Display
    {
        get
        {
            var displayState = State == AwaitingSave ? "待保存 · 接收已完成" : Saved ? "已保存到收件資料夾" : State;
            var details = $"{Label}  ·  {FriendName}\n{displayState}{(Error == null ? "" : " · " + Error)}";
            if (Saved) return details + "\n" + Format(TotalBytes);
            if (TotalBytes <= 0) return details;
            var speed = RecentBytesPerSecond > 0 && State.StartsWith("傳輸中", StringComparison.Ordinal)
                ? $" · {Format((long)RecentBytesPerSecond)}/s" : "";
            return $"{details}\n{Format(DoneBytes)} / {Format(TotalBytes)} · {Math.Clamp(100d * DoneBytes / TotalBytes, 0, 100):0}%{speed}";
        }
    }
    public static string Format(long bytes) => bytes >= 1073741824 ? $"{bytes / 1073741824d:0.00} GB" : bytes >= 1048576 ? $"{bytes / 1048576d:0.0} MB" : bytes >= 1024 ? $"{bytes / 1024d:0.0} KB" : $"{bytes} B";
}
public sealed record IncomingOffer(string Id, string FriendId, string FriendName, string Label, bool Continuous)
{
    public string Display => $"{FriendName} 想傳送：{Label}\n{(Continuous ? "持續資料夾：後續修改與刪除會同步" : "單次傳送：完成後可獨立保存")} · 等待你接受";
}
public sealed class AppState
{
    public int Version { get; set; } = 1;
    public string Inbox { get; set; } = "";
    public bool KeepBackground { get; set; } = true;
    public bool ExplorerHotkey { get; set; }
    public bool ReceiveNotifications { get; set; } = true;
    public HashSet<string> AnnouncedIncoming { get; set; } = new(StringComparer.Ordinal);
    public HashSet<string> DeclinedOffers { get; set; } = new(StringComparer.Ordinal);
    public List<TransferJob> Jobs { get; set; } = [];
}
public sealed class StateStore(string file)
{
    private byte[]? lastSaved;
    public AppState Load()
    {
        if (!File.Exists(file)) return new AppState();
        // Corrupt state is an error, never silently reset an authorization or receipt journal.
        var state = JsonSerializer.Deserialize<AppState>(File.ReadAllText(file)) ?? throw new IOException("工作紀錄無法讀取。請保留設定並從備份恢復。");
        if (state.Version != 1) throw new IOException("工作紀錄版本不支援。");
        return state;
    }
    public void Save(AppState state)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, new JsonSerializerOptions { WriteIndented = true });
        if (lastSaved != null && bytes.AsSpan().SequenceEqual(lastSaved) && File.Exists(file)) return;
        using (var stream = new FileStream(file + ".new", FileMode.Create, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
        if (File.Exists(file)) File.Replace(file + ".new", file, file + ".bak");
        else File.Move(file + ".new", file);
        lastSaved = bytes;
    }
}
