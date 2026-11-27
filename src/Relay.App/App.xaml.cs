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
            MessageBox.Show("操作未完成。\n" + error.Exception.Message, "NearDrop");
            error.Handled = true;
        };
        var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NearDrop", "Data");
        var test = Array.IndexOf(e.Args, "--profile");
        try
        {
            if (test >= 0)
            {
                if (test + 1 == e.Args.Length || e.Args[test + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("缺少 NearDrop 設定資料夾路徑。");
                profile = Path.GetFullPath(e.Args[test + 1]);
            }
            var paths = e.Args.Where((arg, index) => (test < 0 || index != test + 1) &&
                !arg.StartsWith("--", StringComparison.Ordinal)).ToArray();
            var background = e.Args.Contains("--background");
            instance = new SingleInstance(profile);
            if (!instance.TryOwn(profile))
            {
                if (!background || paths.Length > 0) await instance.ForwardAsync(paths);
                Shutdown();
                return;
            }
            var window = new MainWindow(profile, Array.IndexOf(e.Args, "--local-test") >= 0);
            MainWindow = window;
            instance.Listen(p => Dispatcher.InvokeAsync(() => window.ReceiveSelection(p)));
            window.Show();
            if (paths.Length > 0) window.ReceiveSelection(paths);
            else if (background) window.Hide();
        }
        catch (Exception error) { MessageBox.Show(error.Message, "NearDrop 無法啟動"); Shutdown(1); }
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
