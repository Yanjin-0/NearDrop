using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;
using Relay.Core;
using Relay.Windows;

namespace Relay.Setup;

public sealed class InstallRecord
{
    public string Product { get; set; } = "NearDrop-7fb06d16";
    public string Version { get; set; } = typeof(InstallRecord).Assembly.GetName().Version!.ToString(3);
    public List<string> Files { get; set; } = [];
}
public sealed class Installer(string directory, bool isolated)
{
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    private string RecordPath => Path.Combine(DirectoryPath, "neardrop-install.json");
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\NearDrop";
    private void CheckTarget()
    {
        SafePaths.NoLinks(DirectoryPath);
        if (Path.GetPathRoot(DirectoryPath) == DirectoryPath || DirectoryPath.StartsWith("\\\\")) throw new IOException("安裝位置需為本機的專用資料夾。");
        if (Directory.Exists(DirectoryPath) && Directory.EnumerateFileSystemEntries(DirectoryPath).Any() && !File.Exists(RecordPath))
            throw new IOException("安裝位置不是空白的專用資料夾，已停止以保留現有內容。");
        if (File.Exists(RecordPath) && ReadRecord().Product != "NearDrop-7fb06d16") throw new IOException("安裝紀錄不符。");
        foreach (var process in Process.GetProcessesByName("NearDrop"))
        {
            using (process)
            {
                try { if (process.MainModule?.FileName == Path.Combine(DirectoryPath, "NearDrop.exe")) throw new IOException("請先從 NearDrop 系統匣退出並停止背景傳輸，再進行安裝或移除。"); }
                catch (System.ComponentModel.Win32Exception) { /* Another user's process cannot use our private install. */ }
            }
        }
    }
    private InstallRecord ReadRecord() => JsonSerializer.Deserialize<InstallRecord>(File.ReadAllText(RecordPath)) ?? throw new IOException("安裝紀錄已損壞。");
    public async Task InstallAsync(Action<string> progress)
    {
        CheckTarget();
        var assembly = Assembly.GetExecutingAssembly();
        await using var embedded = assembly.GetManifestResourceStream("NearDrop.Payload") ?? throw new IOException("安裝包尚未建置，請執行 scripts/build.ps1。");
        using var hashReader = new StreamReader(assembly.GetManifestResourceStream("NearDrop.PayloadHash")!);
        var expected = (await hashReader.ReadToEndAsync()).Trim();
        var stage = DirectoryPath + ".install-" + Guid.NewGuid().ToString("N");
        PrivateStorage.Create(stage);
        var zipPath = Path.Combine(stage, "payload.zip");
        progress("正在驗證完整安裝包…");
        await using (var zipFile = File.Create(zipPath)) await embedded.CopyToAsync(zipFile);
        await using (var zipFile = File.OpenRead(zipPath))
            if (!Convert.ToHexString(await SHA256.HashDataAsync(zipFile)).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("安裝包完整性驗證失敗。");
        var record = new InstallRecord();
        progress("正在展開應用程式與官方傳輸元件…");
        var extracted = Path.Combine(stage, "app"); Directory.CreateDirectory(extracted);
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue;
                var target = SafePaths.Relative(extracted, entry.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, false); record.Files.Add(entry.FullName.Replace('/', Path.DirectorySeparatorChar));
            }
        }
        await using (var engine = File.OpenRead(Path.Combine(extracted, "engine", "syncthing.exe")))
            if (!Convert.ToHexString(await SHA256.HashDataAsync(engine)).Equals(SyncthingEngine.ExecutableHash, StringComparison.OrdinalIgnoreCase)) throw new IOException("官方引擎驗證失敗。");
        PrivateStorage.Create(DirectoryPath);
        // Persist the owned-file inventory first. Interrupted install can be repaired by rerunning.
        record.Files.Add("Uninstall.exe");
        if (File.Exists(RecordPath)) record.Files = record.Files.Union(ReadRecord().Files, StringComparer.OrdinalIgnoreCase).ToList();
        await File.WriteAllTextAsync(RecordPath, JsonSerializer.Serialize(record));
        foreach (var relative in record.Files.Where(f => f != "Uninstall.exe"))
        {
            var source = SafePaths.Relative(extracted, relative);
            if (!File.Exists(source)) continue;
            var target = SafePaths.Relative(DirectoryPath, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(source, target, true);
        }
        File.Copy(Environment.ProcessPath!, Path.Combine(DirectoryPath, "Uninstall.exe"), true);
        if (!isolated)
        {
            progress("正在建立開始功能表捷徑…");
            ExplorerIntegration.CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "NearDrop.lnk"), Path.Combine(DirectoryPath, "NearDrop.exe"), "");
            using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
            key.SetValue("DisplayName", "NearDrop 安心傳檔"); key.SetValue("DisplayVersion", record.Version); key.SetValue("Publisher", "NearDrop contributors");
            key.SetValue("InstallLocation", DirectoryPath); key.SetValue("DisplayIcon", Path.Combine(DirectoryPath, "NearDrop.exe"));
            key.SetValue("UninstallString", '"' + Path.Combine(DirectoryPath, "Uninstall.exe") + "\" --uninstall");
            key.SetValue("NoModify", 1); key.SetValue("NoRepair", 1);
        }
        // Only exact files extracted by this invocation are removed. Never recursively delete user content.
        File.Delete(zipPath);
        foreach (var relative in record.Files.Where(f => f != "Uninstall.exe"))
        {
            var path = SafePaths.Relative(extracted, relative); if (File.Exists(path)) File.Delete(path);
        }
        RemoveEmptyDirectories(extracted); if (!Directory.EnumerateFileSystemEntries(stage).Any()) Directory.Delete(stage);
        progress("安裝完成。無需另外安裝 .NET 或 Syncthing。");
    }
    public Task UninstallAsync() => Task.Run(() =>
    {
        CheckTarget();
        if (!File.Exists(RecordPath)) throw new IOException("找不到安裝紀錄，不會刪除任何檔案。");
        var record = ReadRecord();
        var ownedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relative in record.Files)
        {
            var path = SafePaths.Relative(DirectoryPath, relative);
            if (File.Exists(path)) File.Delete(path);
            for (var parent = Path.GetDirectoryName(path); parent != null && SafePaths.Within(parent, DirectoryPath); parent = Path.GetDirectoryName(parent))
                ownedDirectories.Add(parent);
        }
        File.Delete(RecordPath);
        if (!isolated)
        {
            RemoveShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "NearDrop.lnk"));
            RemoveShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), "NearDrop.lnk"));
            using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (run?.GetValue("NearDrop") is string startup && startup.StartsWith('"' + Path.Combine(DirectoryPath, "NearDrop.exe") + '"', StringComparison.OrdinalIgnoreCase)) run.DeleteValue("NearDrop", false);
            using var uninstall = Registry.CurrentUser.OpenSubKey(UninstallKey);
            if (uninstall?.GetValue("InstallLocation") as string == DirectoryPath) Registry.CurrentUser.DeleteSubKey(UninstallKey, false);
        }
        foreach (var path in ownedDirectories.OrderByDescending(p => p.Length)) DeleteIfEmpty(path);
        DeleteIfEmpty(DirectoryPath);
        // Never touch %LOCALAPPDATA%/NearDrop/Data, received files, queue snapshots or backups.
    });
    private void RemoveShortcut(string path)
    {
        // This shortcut is ours only if its target matches this installation.
        if (!File.Exists(path)) return;
        var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        object? link = null;
        try
        {
            dynamic value = shell; link = value.CreateShortcut(path); dynamic shortcut = link;
            if (string.Equals((string)shortcut.TargetPath, Path.Combine(DirectoryPath, "NearDrop.exe"), StringComparison.OrdinalIgnoreCase)) File.Delete(path);
        }
        finally { if (link != null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link); System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
    }
    private static void RemoveEmptyDirectories(string root)
    {
        if (!Directory.Exists(root)) return;
        SafePaths.NoLinks(root);
        // Do not descend into junctions, even when only attempting to remove empty folders.
        foreach (var child in Directory.EnumerateDirectories(root)) { SafePaths.NoLinks(child); RemoveEmptyDirectories(child); }
        DeleteIfEmpty(root);
    }
    private static void DeleteIfEmpty(string path)
    {
        SafePaths.NoLinks(path);
        if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
    }
}
