using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Museek.Views;

/// <summary>Small "Search &amp; Play" popup (hotkey: Ctrl+Alt+F).</summary>
public sealed class QuickSearchWindow : Window
{
    private readonly TextBox _box = new() { Padding = new Thickness(6, 4, 6, 4), FontSize = 13 };
    private readonly TextBlock _status = new() { Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Func<string, Task<bool>> _onSearch;

    public QuickSearchWindow(Func<string, Task<bool>> onSearch)
    {
        _onSearch = onSearch;
        Title = "Museek — Search & Play";
        Width = 380; Height = 130;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var play = new Button { Content = "Play", Width = 70, Margin = new Thickness(6, 0, 0, 0) };
        play.Click += async (_, _) => await Submit();
        _box.KeyDown += async (_, e) => { if (e.Key == Key.Enter) await Submit(); else if (e.Key == Key.Escape) Close(); };

        var row = new DockPanel();
        DockPanel.SetDock(play, Dock.Right);
        row.Children.Add(play);
        row.Children.Add(_box);

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(new TextBlock { Text = "Search for a song to play:", Margin = new Thickness(0, 0, 0, 4) });
        root.Children.Add(row);
        root.Children.Add(_status);
        Content = root;

        Loaded += (_, _) => { Activate(); _box.Focus(); };
    }

    private async Task Submit()
    {
        var q = _box.Text.Trim();
        if (q.Length == 0) return;
        _status.Text = "Searching...";
        if (await _onSearch(q)) Close();
        else _status.Text = "Couldn't find or play that — try a different search.";
    }
}
