using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Museek.Services;

namespace Museek.Views;

/// <summary>
/// Condensed settings dialog (the original had many more tabs/options; every
/// value is still editable in settings.json).
/// </summary>
public sealed class SettingsWindow : Window
{
    private readonly Action _onSaved;
    private readonly Action _onReset;
    private readonly Dictionary<string, Func<object>> _readers = new();
    private readonly Dictionary<string, TextBox> _hotkeyBoxes = new();
    private StackPanel _page = null!;

    public SettingsWindow(Action onSaved, Action onReset)
    {
        _onSaved = onSaved; _onReset = onReset;
        Title = "Museek — Settings";
        Width = 480; Height = 680;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Build();
    }

    private void Build()
    {
        var s = SettingsStore.Current;
        var tabs = new TabControl { Margin = new Thickness(8) };

        // ---- General --------------------------------------------------------
        var general = NewPage();
        Section("Spotify & chatbot");
        Combo("source", "Music source (spotify / windows = Apple Music & others)", new[] { "spotify", "windows" },
              s.MusicSource == "windows" ? "windows" : "spotify");
        Text("mediaFilter", "Windows media: only follow this app (blank = auto, e.g. apple, chrome)", s.MediaAppFilter == "auto" ? "" : s.MediaAppFilter);
        Text("clientId", "Spotify Client ID", s.SpotifyClientId);
        Secret("gemini", "Gemini API key (leave blank to keep current)");
        Text("model", "Chatbot model", s.ChatbotModel);
        Check("chatbot", "Enable chatbot", s.ChatbotEnabled);

        Section("Behaviour");
        Number("poll", "Poll interval (ms)", s.PollIntervalMs);
        Number("volStep", "Volume step (%)", s.VolumeStep);
        Check("upNext", "Show 'Up next'", s.ShowUpNext);
        Check("lyrics", "Enable synced lyrics", s.LyricsEnabled);
        Check("quick", "Enable quick search / voice search", s.QuickSearchEnabled);
        Check("autoHide", "Auto-hide when nothing is playing", s.AutoHideWhenIdle);
        Check("notify", "Notify on track change", s.NotifyOnTrackChange);
        Check("update", "Check for updates", s.UpdateCheckEnabled);
        var auto = Check("autostart", "Start with Windows", AutostartService.IsEnabled());
        auto.IsEnabled = AutostartService.IsAvailable;
        tabs.Items.Add(new TabItem { Header = "General", Content = Scroll(general) });

        // ---- Appearance -----------------------------------------------------
        var look = NewPage();
        Section("Appearance");
        Text("accent", "Accent color (#RRGGBB)", s.AccentColor);
        Combo("theme", "Theme", new[] { "dark", "light" }, s.ThemeMode);
        Combo("bgMode", "Background", new[] { "solid", "transparent" }, s.BackgroundMode);
        Text("bgColor", "Background color override", s.BgColorOverride ?? "");
        Text("textColor", "Text color override", s.TextColorOverride ?? "");
        Text("lyricsColor", "Lyrics color", s.LyricsColor);
        Text("font", "Font family", s.FontFamily);
        Number("opacity", "Opacity (0.2 – 1.0)", s.OverlayOpacity);
        Number("radius", "Corner radius", s.CornerRadius);
        Number("border", "Border width", s.BorderWidth);
        Number("progressH", "Progress bar height", s.ProgressBarHeight);
        Check("compact", "Compact mode (hide tabs)", s.CompactMode);
        Check("ambient", "Ambient album-art glow", s.AmbientGlow);
        Check("showLabel", "Show 'Now playing' label", s.ShowNowPlayingLabel);
        Check("showArtist", "Show artist name", s.ShowArtistName);
        Check("showTime", "Show time text", s.ShowTimeText);
        Check("showShuffle", "Show shuffle button", s.ShowShuffleButton);
        Check("showRepeat", "Show repeat button", s.ShowRepeatButton);
        Check("showLike", "Show like button", s.ShowLikeButton);
        Check("showClose", "Show close button", s.ShowCloseButton);
        Check("showLock", "Show lock button", s.ShowLockButton);
        Check("showSettings", "Show settings button", s.ShowSettingsButton);
        tabs.Items.Add(new TabItem { Header = "Appearance", Content = Scroll(look) });

        // ---- Voice ----------------------------------------------------------
        var voice = NewPage();
        Section("Voice commands");
        Check("voice", "Enable voice commands", s.VoiceEnabled);
        Text("wake", "Wake word (always-listening mode only)", s.WakeWord);
        Check("ptt", "Push-to-talk (recommended: far fewer false triggers, much less CPU)", s.PushToTalkEnabled);
        Text("pttKey", "Push-to-talk key", s.PushToTalkKey);
        tabs.Items.Add(new TabItem { Header = "Voice", Content = Scroll(voice) });

        // ---- Hotkeys --------------------------------------------------------
        var keys = NewPage();
        Section("Global hotkeys  (e.g. ctrl+alt+space)");
        foreach (var kv in s.Hotkeys)
        {
            var box = Text("hk_" + kv.Key, kv.Key.Replace('_', ' '), kv.Value);
            _hotkeyBoxes[kv.Key] = box;
        }
        tabs.Items.Add(new TabItem { Header = "Hotkeys", Content = Scroll(keys) });

        // ---- buttons --------------------------------------------------------
        var save = new Button { Content = "Save", Width = 90, Margin = new Thickness(4), IsDefault = true };
        var reset = new Button { Content = "Reset to defaults", Width = 120, Margin = new Thickness(4) };
        var export = new Button { Content = "Export…", Width = 80, Margin = new Thickness(4) };
        var import = new Button { Content = "Import…", Width = 80, Margin = new Thickness(4) };
        save.Click += (_, _) => { Apply(); Close(); };
        reset.Click += (_, _) =>
        {
            if (MessageBox.Show("Reset all settings to defaults?", "Museek", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            SettingsStore.ResetToDefaults(); _onReset(); Close();
        };
        export.Click += (_, _) =>
        {
            var d = new SaveFileDialog { FileName = "museek-settings.json", Filter = "JSON|*.json" };
            if (d.ShowDialog() == true) SettingsStore.Export(d.FileName);
        };
        import.Click += (_, _) =>
        {
            var d = new OpenFileDialog { Filter = "JSON|*.json" };
            if (d.ShowDialog() == true) { SettingsStore.Import(d.FileName); _onSaved(); Close(); }
        };

        var bar = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 8) };
        foreach (var b in new[] { save, reset, export, import }) bar.Children.Add(b);

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);
        root.Children.Add(tabs);
        Content = root;

    }

    private StackPanel NewPage() { _page = new StackPanel { Margin = new Thickness(12) }; return _page; }

    // ------------------------------------------------------------- Apply ----
    private void Apply()
    {
        var s = SettingsStore.Current;

        s.MusicSource = Str("source");
        s.MediaAppFilter = string.IsNullOrWhiteSpace(Str("mediaFilter")) ? "auto" : Str("mediaFilter").Trim();
        s.SpotifyClientId = Str("clientId").Trim();
        var g = Str("gemini"); if (g.Length > 0) s.SetGeminiKey(g);
        s.ChatbotModel = Str("model").Trim();
        s.ChatbotEnabled = Bool("chatbot");

        s.PollIntervalMs = Math.Max(1000, (int)Num("poll", s.PollIntervalMs));
        s.VolumeStep = Math.Clamp((int)Num("volStep", s.VolumeStep), 1, 100);
        s.ShowUpNext = Bool("upNext");
        s.LyricsEnabled = Bool("lyrics");
        s.QuickSearchEnabled = Bool("quick");
        s.AutoHideWhenIdle = Bool("autoHide");
        s.NotifyOnTrackChange = Bool("notify");
        s.UpdateCheckEnabled = Bool("update");
        if (AutostartService.IsAvailable) AutostartService.SetEnabled(Bool("autostart"));

        s.AccentColor = Str("accent").Trim();
        s.ThemeMode = Str("theme");
        s.BackgroundMode = Str("bgMode");
        s.BgColorOverride = NullIfEmpty(Str("bgColor"));
        s.TextColorOverride = NullIfEmpty(Str("textColor"));
        s.LyricsColor = Str("lyricsColor").Trim();
        s.FontFamily = Str("font").Trim();
        s.OverlayOpacity = Num("opacity", s.OverlayOpacity);
        s.CornerRadius = (int)Num("radius", s.CornerRadius);
        s.BorderWidth = (int)Num("border", s.BorderWidth);
        s.ProgressBarHeight = (int)Num("progressH", s.ProgressBarHeight);
        s.CompactMode = Bool("compact");
        s.AmbientGlow = Bool("ambient");
        s.ShowNowPlayingLabel = Bool("showLabel");
        s.ShowArtistName = Bool("showArtist");
        s.ShowTimeText = Bool("showTime");
        s.ShowShuffleButton = Bool("showShuffle");
        s.ShowRepeatButton = Bool("showRepeat");
        s.ShowLikeButton = Bool("showLike");
        s.ShowCloseButton = Bool("showClose");
        s.ShowLockButton = Bool("showLock");
        s.ShowSettingsButton = Bool("showSettings");

        s.VoiceEnabled = Bool("voice");
        s.WakeWord = Str("wake").Trim().ToLowerInvariant();
        s.PushToTalkEnabled = Bool("ptt");
        s.PushToTalkKey = Str("pttKey").Trim();

        foreach (var kv in _hotkeyBoxes)
            if (kv.Value.Text.Trim().Length > 0) s.Hotkeys[kv.Key] = kv.Value.Text.Trim().ToLowerInvariant();

        SettingsStore.Save();
        _onSaved();
    }

    private static string? NullIfEmpty(string v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    private string Str(string k) => _readers[k]() as string ?? "";
    private bool Bool(string k) => _readers[k]() is bool b && b;
    private double Num(string k, double fallback) =>
        double.TryParse(Str(k), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    // ---------------------------------------------------------- UI helpers ---
    private static ScrollViewer Scroll(StackPanel p) => new() { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

    private void Section(string title) =>
        _page.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 10, 0, 4) });

    private TextBox Text(string key, string label, string value)
    {
        var tb = new TextBox { Text = value, Padding = new Thickness(4, 3, 4, 3), Margin = new Thickness(0, 0, 0, 6) };
        AddRow(label, tb);
        _readers[key] = () => tb.Text;
        return tb;
    }

    private void Number(string key, string label, double value)
    {
        var tb = new TextBox { Text = value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Padding = new Thickness(4, 3, 4, 3), Margin = new Thickness(0, 0, 0, 6) };
        AddRow(label, tb);
        _readers[key] = () => tb.Text;
    }

    private void Secret(string key, string label)
    {
        var pb = new PasswordBox { Padding = new Thickness(4, 3, 4, 3), Margin = new Thickness(0, 0, 0, 6) };
        AddRow(label, pb);
        _readers[key] = () => pb.Password;
    }

    private void Combo(string key, string label, string[] items, string selected)
    {
        var cb = new ComboBox { ItemsSource = items, SelectedItem = selected, Margin = new Thickness(0, 0, 0, 6) };
        AddRow(label, cb);
        _readers[key] = () => cb.SelectedItem as string ?? items[0];
    }

    private CheckBox Check(string key, string label, bool value)
    {
        var cb = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 2, 0, 4) };
        _page.Children.Add(cb);
        _readers[key] = () => cb.IsChecked == true;
        return cb;
    }

    private void AddRow(string label, FrameworkElement input)
    {
        _page.Children.Add(new TextBlock { Text = label, FontSize = 11, Opacity = 0.7 });
        _page.Children.Add(input);
    }
}
