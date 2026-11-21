using System;
using System.IO;
using System.Windows;
using System.Linq;
using Relay.Windows;

namespace Relay.App;

public partial class App : Application
{
    private SingleInstance? instance;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10)) { MessageBox.Show("需要 Windows 10 或 11。"); Shutdown(); return; }
        DispatcherUnhandledException += (_, error) =>
        {
            MessageBox.Show("操作未完成。請重新啟動；已排入的檔案仍會保留。\n" + error.Exception.Message, "NearDrop");
            error.Handled = true;
        };
        var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NearDrop", "Data");
        var test = Array.IndexOf(e.Args, "--profile");
        if (test >= 0 && test + 1 < e.Args.Length) profile = Path.GetFullPath(e.Args[test + 1]);
        try
        {
            var paths = e.Args.Where(a => !a.StartsWith("--") && (File.Exists(a) || Directory.Exists(a)) && Path.GetFullPath(a) != profile).ToArray();
            instance = new SingleInstance(profile);
            if (!instance.TryOwn(profile)) { await instance.ForwardAsync(paths); Shutdown(); return; }
            var window = new MainWindow(profile, Array.IndexOf(e.Args, "--local-test") >= 0);
            MainWindow = window;
            instance.Listen(p => Dispatcher.InvokeAsync(() => window.ReceiveSelection(p)));
            window.Show();
            if (paths.Length > 0) window.ReceiveSelection(paths);
            else if (Array.IndexOf(e.Args, "--background") >= 0) window.Hide();
        }
        catch (Exception error) { MessageBox.Show(error.Message, "NearDrop 無法啟動"); Shutdown(1); }
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
