using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Relay.Windows;

public static class ExplorerIntegration
{
    private const string StartupKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static string StartupCommand => '"' + Environment.ProcessPath! + "\" --background";
    public static bool StartupEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupKey);
            return string.Equals(key?.GetValue("NearDrop") as string, StartupCommand, StringComparison.OrdinalIgnoreCase);
        }
    }
    public static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(StartupKey);
        if (enabled) key.SetValue("NearDrop", StartupCommand);
        else if (string.Equals(key.GetValue("NearDrop") as string, StartupCommand, StringComparison.OrdinalIgnoreCase))
            key.DeleteValue("NearDrop", false);
    }
    public static void InstallSendTo()
    {
        var target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), "NearDrop.lnk");
        CreateShortcut(target, Environment.ProcessPath!, "");
    }
    public static void CreateShortcut(string shortcut, string executable, string arguments)
    {
        var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        object? link = null;
        try
        {
            dynamic automation = shell;
            link = automation.CreateShortcut(shortcut);
            dynamic value = link; value.TargetPath = executable; value.Arguments = arguments;
            value.WorkingDirectory = Path.GetDirectoryName(executable); value.Description = "NearDrop 安心傳檔"; value.Save();
        }
        finally { if (link != null) Marshal.FinalReleaseComObject(link); Marshal.FinalReleaseComObject(shell); }
    }
    public static string[] SelectedExplorerFiles()
    {
        var foreground = GetForegroundWindow();
        object? shell = null, windows = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
            dynamic automation = shell!; windows = automation.Windows(); dynamic collection = windows;
            for (var i = 0; i < (int)collection.Count; i++)
            {
                object? window = null, document = null, selection = null;
                try
                {
                    window = collection.Item(i); dynamic browser = window;
                    if (new IntPtr((long)browser.HWND) != foreground) continue;
                    document = browser.Document; dynamic folder = document;
                    selection = folder.SelectedItems(); dynamic items = selection;
                    var result = new List<string>();
                    for (var j = 0; j < (int)items.Count; j++)
                    {
                        object item = items.Item(j);
                        try { dynamic entry = item; string path = entry.Path; if (File.Exists(path) || Directory.Exists(path)) result.Add(path); }
                        finally { Marshal.FinalReleaseComObject(item); }
                    }
                    return result.ToArray();
                }
                finally
                {
                    if (selection != null) Marshal.FinalReleaseComObject(selection);
                    if (document != null) Marshal.FinalReleaseComObject(document);
                    if (window != null) Marshal.FinalReleaseComObject(window);
                }
            }
            return [];
        }
        finally { if (windows != null) Marshal.FinalReleaseComObject(windows); if (shell != null) Marshal.FinalReleaseComObject(shell); }
    }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool UnregisterHotKey(IntPtr window, int id);
}
