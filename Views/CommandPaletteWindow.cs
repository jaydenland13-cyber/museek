using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Museek.Services;

namespace Museek.Views;

public sealed record PaletteItem(string Title, string Hint, Action Run, string Keywords = "");

/// <summary>
/// A quick "type what you want" box (Ctrl+Alt+P): commands, settings, and free text that can be played, searched or asked.
/// Opens on demand and closes when it loses focus, so it costs nothing while idle.
/// </summary>
public sealed class CommandPaletteWindow : Window
{
    private readonly Func<string, List<PaletteItem>> _provider;
    private readonly TextBox _box;
    private readonly StackPanel _list = new();
    private readonly List<Border> _rows = new();
    private List<PaletteItem> _items = new();
    private int _sel;
    private bool _closing;

    public CommandPaletteWindow(ResourceDictionary theme, Func<string, List<PaletteItem>> provider)
    {
        _provider = provider;
        try { Resources.MergedDictionaries.Add(theme); }
        catch { foreach (System.Collections.DictionaryEntry de in theme) Resources[de.Key] = de.Value; }

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 580;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily(SettingsStore.Current.EffectiveFont);
        SetResourceReference(ForegroundProperty, "Fg");

        _box = new TextBox { Style = (Style)FindResource("GlassBox"), FontSize = 15, Padding = new Thickness(12, 10, 12, 10) };
        _box.TextChanged += (_, _) => Refresh();

        var hint = new TextBlock
        {
            Text = "Type a command, a song or a question   ·   ↑ ↓ choose   ·   Enter run   ·   Esc close",
            FontSize = 10.5, Margin = new Thickness(4, 8, 0, 6),
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "FgFaint");

        var stack = new StackPanel { Margin = new Thickness(14) };
        stack.Children.Add(_box);
        stack.Children.Add(hint);
        stack.Children.Add(_list);

        var panel = new Border { CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(1), Child = stack };
        panel.SetResourceReference(Border.BackgroundProperty, "Panel");
        panel.SetResourceReference(Border.BorderBrushProperty, "Accent");

        var root = new Grid { Margin = new Thickness(16) };
        root.Children.Add(new Border   // cheap fake shadow
        {
            CornerRadius = new CornerRadius(22), Margin = new Thickness(-3, -1, -3, -7),
            Background = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)),
        });
        root.Children.Add(panel);
        Content = root;

        Loaded += (_, _) =>
        {
            Top = Math.Max(40, SystemParameters.WorkArea.Height * 0.16);
            Activate();
            _box.Focus();
            Keyboard.Focus(_box);
            Refresh();
        };
        PreviewKeyDown += OnKey;
        Deactivated += (_, _) => CloseOnce();
    }

    private void Refresh()
    {
        _items = _provider(_box.Text);
        _sel = 0;
        _list.Children.Clear();
        _rows.Clear();

        if (_items.Count == 0)
        {
            var none = new TextBlock { Text = "No matches", FontSize = 12, Margin = new Thickness(6, 8, 0, 4) };
            none.SetResourceReference(TextBlock.ForegroundProperty, "FgFaint");
            _list.Children.Add(none);
            return;
        }

        for (var i = 0; i < _items.Count; i++)
        {
            var idx = i;
            var item = _items[i];
            var row = new Border
            {
                CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 1, 0, 1),
                Cursor = Cursors.Hand, Background = Brushes.Transparent,
            };
            var grid = new Grid();
            var title = new TextBlock { Text = item.Title, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 90, 0) };
            title.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            var kind = new TextBlock { Text = item.Hint, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            kind.SetResourceReference(TextBlock.ForegroundProperty, "FgFaint");
            grid.Children.Add(title);
            grid.Children.Add(kind);
            row.Child = grid;

            row.MouseEnter += (_, _) => { _sel = idx; Highlight(); };
            row.MouseLeftButtonUp += (_, _) => RunItem(item);
            _rows.Add(row);
            _list.Children.Add(row);
        }
        Highlight();
    }

    private void Highlight()
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            if (i == _sel) _rows[i].SetResourceReference(Border.BackgroundProperty, "AccentSoft");
            else _rows[i].Background = Brushes.Transparent;
        }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: CloseOnce(); e.Handled = true; break;
            case Key.Down: Move(+1); e.Handled = true; break;
            case Key.Up: Move(-1); e.Handled = true; break;
            case Key.Enter:
                if (_items.Count > 0) RunItem(_items[_sel]);
                e.Handled = true;
                break;
        }
    }

    private void Move(int delta)
    {
        if (_items.Count == 0) return;
        _sel = (_sel + delta + _items.Count) % _items.Count;
        Highlight();
    }

    private void RunItem(PaletteItem item)
    {
        CloseOnce();
        Application.Current.Dispatcher.BeginInvoke(item.Run, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void CloseOnce()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }
}
