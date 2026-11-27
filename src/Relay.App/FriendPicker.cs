using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using Relay.Core;

namespace Relay.App;

internal sealed class FriendPicker : Window
{
    private readonly ListBox friends;
    public Friend? Selected => friends.SelectedItem as Friend;
    public FriendPicker(IReadOnlyList<Friend> items)
    {
        Style = (Style)FindResource(typeof(Window));
        Title = "選取收件朋友"; Width = 460; Height = 360; MinWidth = 360; MinHeight = 260; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var grid = new Grid { Margin = new Thickness(24) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var label = new TextBlock { Text = items.Count == 0 ? "請先到「我的裝置」加入朋友。" : "這個資料夾要傳給誰？", Margin = new Thickness(0, 0, 0, 16) };
        grid.Children.Add(label);
        friends = new ListBox { ItemsSource = items, DisplayMemberPath = "Display" }; Grid.SetRow(friends, 1); grid.Children.Add(friends);
        var actions = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        var button = new Button { Content = "選取朋友", IsDefault = true, IsEnabled = false };
        button.Click += (_, _) => { if (Selected != null) DialogResult = true; };
        friends.SelectionChanged += (_, _) => button.IsEnabled = Selected != null;
        actions.Children.Add(button);
        actions.Children.Add(new Button { Content = "取消", IsCancel = true });
        Grid.SetRow(actions, 2); grid.Children.Add(actions);
        if (items.Count == 1) friends.SelectedIndex = 0;
        Content = grid;
    }
}
