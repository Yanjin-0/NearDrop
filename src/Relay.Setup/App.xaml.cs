using System.Diagnostics;
using System.Windows;

namespace Relay.Setup;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            string? Argument(string name) { var i = Array.IndexOf(e.Args, name); return i >= 0 && i + 1 < e.Args.Length ? e.Args[i + 1] : null; }
            var folder = Argument("--install-dir") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "NearDrop");
            var uninstall = e.Args.Contains("--uninstall");
            var isolated = e.Args.Contains("--isolated"); // Test only: no registry, shortcuts or user profile changes.
            if (uninstall && int.TryParse(Argument("--wait-pid"), out var previousPid))
            {
                try
                {
                    using var previous = Process.GetProcessById(previousPid);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await previous.WaitForExitAsync(timeout.Token);
                }
                catch (ArgumentException) { }
            }
            if (uninstall && Path.GetFullPath(Environment.ProcessPath!).StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                var temp = Path.Combine(Path.GetTempPath(), "NearDrop-uninstall-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temp);
                var copy = Path.Combine(temp, "NearDrop-Uninstall.exe"); File.Copy(Environment.ProcessPath!, copy);
                var start = new ProcessStartInfo(copy) { UseShellExecute = false, CreateNoWindow = true };
                foreach (var arg in e.Args) start.ArgumentList.Add(arg);
                start.ArgumentList.Add("--wait-pid");
                start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Process.Start(start); Shutdown(); return;
            }
            var installer = new Installer(folder, isolated);
            if (e.Args.Contains("--silent"))
            {
                if (uninstall) await installer.UninstallAsync(); else await installer.InstallAsync(_ => { });
                Shutdown(0); return;
            }
            MainWindow = new SetupWindow(installer, uninstall); MainWindow.Show();
        }
        catch (Exception error)
        {
            if (e.Args.Contains("--silent"))
            {
                var result = Array.IndexOf(e.Args, "--result");
                if (result >= 0 && result + 1 < e.Args.Length) File.WriteAllText(e.Args[result + 1], error.ToString());
            }
            else MessageBox.Show(error.Message, "NearDrop 安裝未完成");
            Shutdown(1);
        }
    }
}
