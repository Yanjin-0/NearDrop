using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Relay.Setup;

public sealed class SetupWindow : Window
{
    public SetupWindow(Installer installer, bool uninstall)
    {
        Title = uninstall ? "移除 NearDrop" : "安裝 NearDrop"; Width = 620; Height = 450; MinWidth = 540; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; Background = new SolidColorBrush(Color.FromRgb(24, 29, 34));
        Foreground = Brushes.WhiteSmoke; FontFamily = new FontFamily("Segoe UI, Microsoft JhengHei UI"); FontSize = 15;
        var panel = new StackPanel { Margin = new Thickness(38) };
        panel.Children.Add(new TextBlock { Text = "NearDrop", FontSize = 32, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = uninstall ? "移除程式，保留你的檔案。" : "讓檔案，安心抵達。", Margin = new Thickness(0, 8, 0, 26) });
        panel.Children.Add(new TextBlock { Text = uninstall ? "只移除這次安裝的程式與捷徑。你的裝置身分、傳輸暫存、收件與備份都會保留。" : "為目前的 Windows 使用者安裝，無需管理員權限。已包含 .NET 與官方 Syncthing；不需帳號或額外下載。", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = installer.DirectoryPath, Foreground = Brushes.Silver, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 18, 0, 18) });
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12) }; panel.Children.Add(message);
        var progress = new ProgressBar { Height = 3, Visibility = Visibility.Collapsed, IsIndeterminate = true }; panel.Children.Add(progress);
        var button = new Button { Content = uninstall ? "移除程式並保留資料" : "安裝 NearDrop", Padding = new Thickness(20, 10, 20, 10), Margin = new Thickness(0, 22, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        var finished = false; var working = false;
        button.Click += async (_, _) =>
        {
            if (finished)
            {
                try
                {
                    if (!uninstall) Process.Start(new ProcessStartInfo(Path.Combine(installer.DirectoryPath, "NearDrop.exe")) { UseShellExecute = false });
                    Application.Current.Shutdown();
                }
                catch (Exception error)
                {
                    message.Text = "安裝已完成，但無法開啟 NearDrop。\n" + error.Message;
                    button.Content = "重試開啟 NearDrop";
                }
                return;
            }
            working = true; button.IsEnabled = false; progress.Visibility = Visibility.Visible;
            try
            {
                if (uninstall) { await installer.UninstallAsync(); message.Text = "程式已移除。NearDrop 的收件、暫存與裝置設定已保留。"; }
                else await installer.InstallAsync(text => message.Text = text);
                finished = true; button.Content = uninstall ? "完成" : "開啟 NearDrop";
            }
            catch (Exception error)
            {
                message.Text = (uninstall ? "移除未完成。" : "安裝未完成。") + "\n" + error.Message;
                button.Content = uninstall ? "重試移除" : "重試安裝";
            }
            finally { working = false; progress.Visibility = Visibility.Collapsed; button.IsEnabled = true; }
        };
        panel.Children.Add(button); Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Closing += (_, e) => { if (working) e.Cancel = true; else Application.Current.Shutdown(); };
    }
}
