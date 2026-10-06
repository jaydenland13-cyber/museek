using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Museek.Services;

namespace Museek.Views;

public enum WelcomeMode { FirstRun, SignIn }

/// <summary>
/// The start-up screen. First run: connect your music (Spotify sign-in or Apple Music / Windows media), choose a look,
/// finishing touches. Later starts: a short "Welcome back" sign-in screen that continues by itself after 3 seconds
/// once you're signed in (turn it off in Customize → Account).
/// </summary>
public sealed class WelcomeWindow : Window
{
    private static readonly Color Purple = Color.FromRgb(0x8B, 0x5C, 0xF6), Pink = Color.FromRgb(0xEC, 0x48, 0x99), Cyan = Color.FromRgb(0x06, 0xB6, 0xD4);
    private static readonly Color Ink = Color.FromRgb(0x0F, 0x17, 0x2A);
    private static readonly FontFamily Ui = new("Segoe UI Variable Text, Segoe UI");

    private readonly MusicHub _music;
    private readonly WelcomeMode _mode;
    private readonly Grid _pageHost = new();
    private readonly StackPanel _stepDots = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

    private string _source;
    private int _page;
    private string _clientIdValue, _geminiValue = "";
    private TextBlock? _status;
    private Border? _signInBtn, _cancelBtn;
    private CancellationTokenSource? _cts;
    private DispatcherTimer? _auto;
    private int _countdown = 3;
    private bool _closed;
    private TextBlock? _countLabel;

    public WelcomeWindow(MusicHub music, WelcomeMode mode)
    {
        _music = music;
        _mode = mode;
        var s = SettingsStore.Current;
        _source = s.MusicSource == "windows" ? "windows" : "spotify";
        _clientIdValue = s.SpotifyClientId;

        Title = "Museek";
        Width = 840; Height = 540;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        FontFamily = Ui;
        Foreground = Brushes.White;

        var root = new Border
        {
            CornerRadius = new CornerRadius(22), Background = Solid(Ink), ClipToBounds = true,
            BorderBrush = Solid(Color.FromArgb(0x33, 255, 255, 255)), BorderThickness = new Thickness(1),
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(BuildBrandPane());

        var right = new Grid { Margin = new Thickness(40, 30, 40, 28) };
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition());
        var top = new Grid { Background = Brushes.Transparent };
        top.MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { } };
        top.Children.Add(_stepDots);
        var close = new TextBlock
        {
            Text = "✕", FontSize = 13, Foreground = Solid(Color.FromArgb(0x99, 255, 255, 255)), Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, -4, -22, 0), ToolTip = "Close",
        };
        close.MouseLeftButtonDown += (_, e) => e.Handled = true;      // don't start a window drag from the close button
        close.MouseLeftButtonUp += (_, _) => { DialogResult = false; };
        top.Children.Add(close);
        Grid.SetRow(_pageHost, 1);
        right.Children.Add(top);
        right.Children.Add(_pageHost);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        root.Child = grid;
        Content = root;

        PreviewMouseDown += (_, _) => StopAutoContinue();
        PreviewKeyDown += (_, _) => StopAutoContinue();
        Closed += (_, _) => { _closed = true; _cts?.Cancel(); _auto?.Stop(); };

        ShowPage(0);
    }

    // =========================================================== brand pane ====
    private UIElement BuildBrandPane()
    {
        var pane = new Border
        {
            CornerRadius = new CornerRadius(22, 0, 0, 22), Padding = new Thickness(34, 40, 30, 30),
            Background = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(Color.FromRgb(0x5B, 0x21, 0xB6), 0), new GradientStop(Color.FromRgb(0xA2, 0x1C, 0xAF), 0.55),
                    new GradientStop(Color.FromRgb(0xBE, 0x18, 0x5D), 1),
                }, 150),
        };
        pane.MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { } };
        var st = new StackPanel();
        st.Children.Add(new Border
        {
            Width = 64, Height = 64, CornerRadius = new CornerRadius(32), HorizontalAlignment = HorizontalAlignment.Left,
            Background = Solid(Color.FromArgb(0x33, 255, 255, 255)), BorderBrush = Solid(Color.FromArgb(0x66, 255, 255, 255)), BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = "♫", FontSize = 32, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        });
        st.Children.Add(Label("Museek", 34, Brushes.White, FontWeights.Bold, 22));
        st.Children.Add(Label("Your music, floating above everything.", 14, Solid(Color.FromArgb(0xE0, 255, 255, 255)), top: 6));

        foreach (var line in new[] { "Spotify and Apple Music", "Lyrics, widgets and voice control", "Eight looks, fully customizable", "Light on your PC — built to stay out of the way" })
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
            row.Children.Add(new TextBlock { Text = "✓", FontSize = 13, Foreground = Brushes.White, Margin = new Thickness(0, 0, 10, 0), FontWeight = FontWeights.Bold });
            row.Children.Add(new TextBlock { Text = line, FontSize = 12.5, Foreground = Solid(Color.FromArgb(0xE6, 255, 255, 255)), TextWrapping = TextWrapping.Wrap, MaxWidth = 190 });
            st.Children.Add(row);
        }

        var wrapper = new Grid();
        wrapper.Children.Add(st);
        wrapper.Children.Add(new TextBlock
        {
            Text = $"v{AppInfo.Version}\nNot affiliated with Spotify or Apple.", FontSize = 10.5, LineHeight = 15,
            Foreground = Solid(Color.FromArgb(0xAA, 255, 255, 255)), VerticalAlignment = VerticalAlignment.Bottom,
        });
        pane.Child = wrapper;
        return pane;
    }

    // ============================================================== pages ====
    private int PageCount => _mode == WelcomeMode.FirstRun ? 3 : 1;

    private void ShowPage(int page)
    {
        _page = page;
        StopAutoContinue();
        _pageHost.Children.Clear();
        _status = null; _signInBtn = null; _cancelBtn = null; _countLabel = null;

        _stepDots.Children.Clear();
        if (_mode == WelcomeMode.FirstRun)
            for (var i = 0; i < PageCount; i++)
                _stepDots.Children.Add(new Border
                {
                    Width = i == page ? 22 : 8, Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 6, 0),
                    Background = i == page ? Solid(Purple) : Solid(Color.FromArgb(0x44, 255, 255, 255)),
                });

        _pageHost.Children.Add(_mode == WelcomeMode.SignIn ? BuildReturningPage()
                              : page == 0 ? BuildSourcePage()
                              : page == 1 ? BuildLookPage()
                              : BuildFinishPage());
    }

    // ---- page: connect your music ------------------------------------------------
    private UIElement BuildSourcePage()
    {
        var st = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        st.Children.Add(Label("Connect your music", 26, Brushes.White, FontWeights.Bold));
        st.Children.Add(Label("Pick where you listen. You can switch any time in Customize.", 13, Dim, top: 6));

        var cards = new Grid { Margin = new Thickness(0, 22, 0, 0) };
        cards.ColumnDefinitions.Add(new ColumnDefinition());
        cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        cards.ColumnDefinitions.Add(new ColumnDefinition());
        var c1 = SourceCard("spotify", "Spotify", "Full control: queue, search, likes, volume", "S");
        var c2 = SourceCard("windows", "Apple Music & others", "Follows what Windows is playing — no account", "♪");
        Grid.SetColumn(c2, 2);
        cards.Children.Add(c1); cards.Children.Add(c2);
        st.Children.Add(cards);

        var detail = new StackPanel { Margin = new Thickness(0, 20, 0, 0) };
        if (_source == "spotify") detail.Children.Add(BuildSpotifyDetail());
        else
        {
            detail.Children.Add(Label("No sign-in needed. Open the Apple Music app (or music.apple.com in your browser), play a song and Museek picks it up. " +
                                      "Windows doesn't share the queue, search or likes, so those stay hidden.", 12.5, Dim));
        }
        st.Children.Add(detail);

        var nav = new Grid { Margin = new Thickness(0, 24, 0, 0) };
        var canContinue = _source == "windows" || _music.SpotifyConnected;
        var skip = Label(_source == "spotify" && !_music.SpotifyConnected ? "Skip sign-in for now" : "", 12, Dim);
        skip.Cursor = Cursors.Hand; skip.VerticalAlignment = VerticalAlignment.Center;
        skip.MouseLeftButtonUp += (_, _) => { SaveSource(); ShowPage(1); };
        var next = Button("Next", primary: true, () => { SaveSource(); ShowPage(1); });
        next.HorizontalAlignment = HorizontalAlignment.Right;
        Enable(next, canContinue);
        nav.Children.Add(skip); nav.Children.Add(next);
        st.Children.Add(nav);
        return st;
    }

    private Border SourceCard(string key, string title, string sub, string glyph)
    {
        var selected = _source == key;
        var b = new Border
        {
            CornerRadius = new CornerRadius(14), Padding = new Thickness(16, 14, 16, 14), Cursor = Cursors.Hand,
            BorderThickness = new Thickness(selected ? 2 : 1),
            BorderBrush = selected ? Solid(Purple) : Solid(Color.FromArgb(0x33, 255, 255, 255)),
            Background = selected ? Solid(Color.FromArgb(0x26, 0x8B, 0x5C, 0xF6)) : Solid(Color.FromArgb(0x0D, 255, 255, 255)),
        };
        var st = new StackPanel();
        st.Children.Add(new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(17), HorizontalAlignment = HorizontalAlignment.Left,
            Background = new LinearGradientBrush(Purple, Pink, 45),
            Child = new TextBlock { Text = glyph, FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        });
        st.Children.Add(Label(title, 15, Brushes.White, FontWeights.SemiBold, 10));
        st.Children.Add(Label(sub, 11.5, Dim, top: 3));
        b.Child = st;
        b.MouseLeftButtonUp += (_, _) => { _source = key; ShowPage(0); };
        return b;
    }

    private UIElement BuildSpotifyDetail()
    {
        var st = new StackPanel();
        if (_music.SpotifyConnected)
        {
            st.Children.Add(Label($"✓ Signed in as {_music.UserName ?? "your Spotify account"}", 13.5, Solid(Color.FromRgb(0x34, 0xD3, 0x99)), FontWeights.SemiBold));
            var out1 = Label("Use a different account", 12, Dim, top: 6);
            out1.Cursor = Cursors.Hand;
            out1.MouseLeftButtonUp += (_, _) => { _music.SignOutSpotify(); ShowPage(0); };
            st.Children.Add(out1);
            return st;
        }

        st.Children.Add(Label("Spotify Client ID", 12, Dim));
        st.Children.Add(Field("Paste your Client ID", _clientIdValue, false, v => _clientIdValue = v));
        var help = new TextBlock { FontSize = 11.5, Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), LineHeight = 17 };
        help.Inlines.Add("Create a free app at ");
        var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run("developer.spotify.com/dashboard")) { NavigateUri = new Uri("https://developer.spotify.com/dashboard"), Foreground = Solid(Cyan) };
        link.RequestNavigate += (_, e) => { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); e.Handled = true; };
        help.Inlines.Add(link);
        help.Inlines.Add(", add the redirect URI  http://127.0.0.1:8888/callback  and copy its Client ID here. (Spotify requires Premium for the app owner.)");
        st.Children.Add(help);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        _signInBtn = Button("Sign in with Spotify", primary: true, () => _ = SignInAsync());
        _cancelBtn = Button("Cancel", primary: false, () => _cts?.Cancel());
        _cancelBtn.Margin = new Thickness(10, 0, 0, 0);
        _cancelBtn.Visibility = Visibility.Collapsed;
        row.Children.Add(_signInBtn); row.Children.Add(_cancelBtn);
        st.Children.Add(row);
        _status = Label("", 12, Dim, top: 10);
        st.Children.Add(_status);
        return st;
    }

    // ---- page: choose your look ----------------------------------------------------
    private UIElement BuildLookPage()
    {
        var st = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        st.Children.Add(Label("Choose your look", 26, Brushes.White, FontWeights.Bold));
        st.Children.Add(Label("Tap one to preview it for real. Every detail stays adjustable later.", 13, Dim, top: 6));
        var current = SettingsStore.Current.Look;
        var gallery = LookGallery.Build(current, p =>
        {
            Looks.Apply(SettingsStore.Current, p);
            SettingsStore.Save();
            ShowPage(1);
        }, 108, Brushes.White, Dim, Solid(Purple));
        gallery.Margin = new Thickness(-4, 18, 0, 0);
        st.Children.Add(gallery);
        st.Children.Add(NavBar(back: () => ShowPage(0), nextText: "Next", next: () => ShowPage(2)));
        return st;
    }

    // ---- page: finishing touches ------------------------------------------------------
    private UIElement BuildFinishPage()
    {
        var s = SettingsStore.Current;
        var st = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        st.Children.Add(Label("Almost there", 26, Brushes.White, FontWeights.Bold));
        st.Children.Add(Label("Two optional extras, then you're in.", 13, Dim, top: 6));

        st.Children.Add(Label("Gemini API key — turns on the chatbot and mood requests", 12, Dim, top: 22));
        st.Children.Add(Field("Optional — aistudio.google.com/apikey", _geminiValue, true, v => _geminiValue = v));

        var toggles = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        if (AutostartService.IsAvailable)
            toggles.Children.Add(Toggle("Start Museek with Windows", null, AutostartService.IsEnabled(), v => AutostartService.SetEnabled(v)));
        toggles.Children.Add(Toggle("Show this screen when Museek starts", "It continues automatically after 3 seconds", s.ShowSignInOnStart,
                                    v => { s.ShowSignInOnStart = v; SettingsStore.Save(); }));
        st.Children.Add(toggles);

        st.Children.Add(NavBar(back: () => ShowPage(1), nextText: "Start Museek", next: Finish));
        return st;
    }

    private void Finish()
    {
        var s = SettingsStore.Current;
        if (_geminiValue.Trim().Length > 0) s.SetGeminiKey(_geminiValue);
        s.MusicSource = _source;
        if (_clientIdValue.Trim().Length > 0) s.SpotifyClientId = _clientIdValue.Trim();
        s.WelcomeDone = true;
        SettingsStore.Save();
        DialogResult = true;
    }

    // ---- returning user ------------------------------------------------------------
    private UIElement BuildReturningPage()
    {
        var s = SettingsStore.Current;
        var windows = s.MusicSource == "windows";
        var ready = windows || _music.SpotifyConnected;

        var st = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var name = windows ? "Apple Music & others" : _music.UserName;
        var initial = string.IsNullOrWhiteSpace(name) ? "♫" : name![..1].ToUpperInvariant();
        st.Children.Add(new Border
        {
            Width = 64, Height = 64, CornerRadius = new CornerRadius(32), HorizontalAlignment = HorizontalAlignment.Left,
            Background = new LinearGradientBrush(Purple, Pink, 45),
            Child = new TextBlock { Text = initial, FontSize = 28, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        });

        if (ready)
        {
            st.Children.Add(Label(windows ? "Welcome back" : $"Welcome back, {name ?? "friend"}", 28, Brushes.White, FontWeights.Bold, 18));
            st.Children.Add(Label(windows ? "Following whatever Windows is playing." : "Signed in to Spotify.", 13.5, Dim, top: 6));

            var go = Button("Continue", primary: true, () => DialogResult = true);
            go.HorizontalAlignment = HorizontalAlignment.Left; go.Margin = new Thickness(0, 26, 0, 0);
            st.Children.Add(go);

            _countLabel = Label("", 11.5, Dim, top: 10);
            st.Children.Add(_countLabel);
            if (s.ShowSignInOnStart) StartAutoContinue();
        }
        else
        {
            st.Children.Add(Label("Sign in to continue", 28, Brushes.White, FontWeights.Bold, 18));
            st.Children.Add(Label("Museek needs you to sign in to Spotify again.", 13.5, Dim, top: 6));
            var box = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
            box.Children.Add(Label("Spotify Client ID", 12, Dim));
            box.Children.Add(Field("Paste your Client ID", _clientIdValue, false, v => _clientIdValue = v));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
            _signInBtn = Button("Sign in with Spotify", primary: true, () => _ = SignInAsync());
            _cancelBtn = Button("Cancel", primary: false, () => _cts?.Cancel());
            _cancelBtn.Margin = new Thickness(10, 0, 0, 0); _cancelBtn.Visibility = Visibility.Collapsed;
            row.Children.Add(_signInBtn); row.Children.Add(_cancelBtn);
            box.Children.Add(row);
            _status = Label("", 12, Dim, top: 10);
            box.Children.Add(_status);
            st.Children.Add(box);
        }

        var links = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 22, 0, 0) };
        if (windows)
        {
            var sp = Label("Use Spotify instead", 12, Solid(Cyan)); sp.Cursor = Cursors.Hand;
            sp.MouseLeftButtonUp += (_, _) => { s.MusicSource = "spotify"; SettingsStore.Save(); ShowPage(0); };
            links.Children.Add(sp);
        }
        else
        {
            var ap = Label("Use Apple Music / Windows media instead", 12, Solid(Cyan)); ap.Cursor = Cursors.Hand;
            ap.MouseLeftButtonUp += (_, _) => { s.MusicSource = "windows"; SettingsStore.Save(); DialogResult = true; };
            links.Children.Add(ap);
            if (_music.SpotifyConnected)
            {
                var so = Label("Sign out", 12, Dim); so.Cursor = Cursors.Hand; so.Margin = new Thickness(18, 0, 0, 0);
                so.MouseLeftButtonUp += (_, _) => { _music.SignOutSpotify(); ShowPage(0); };
                links.Children.Add(so);
            }
        }
        st.Children.Add(links);

        st.Children.Add(Toggle("Show this screen when Museek starts", null, s.ShowSignInOnStart, v => { s.ShowSignInOnStart = v; SettingsStore.Save(); }, topMargin: 26));
        return st;
    }

    // ======================================================== sign-in logic ====
    private async Task SignInAsync()
    {
        var id = _clientIdValue.Trim();
        if (id.Length == 0) { SetStatus("Paste your Spotify Client ID first — the steps are just above.", bad: true); return; }

        var s = SettingsStore.Current;
        s.SpotifyClientId = id; s.MusicSource = "spotify";
        SettingsStore.Save();

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        SetBusy(true);
        SetStatus("Opening Spotify in your browser — approve access, then come back here.", bad: false);
        var ok = await _music.SignInSpotifyAsync(_cts.Token);
        if (_closed) return;
        SetBusy(false);

        if (ok)
        {
            SetStatus($"✓ Signed in as {_music.UserName ?? "your Spotify account"}", bad: false, good: true);
            await Task.Delay(700);
            if (_closed) return;
            if (_mode == WelcomeMode.SignIn) DialogResult = true;
            else ShowPage(0);
        }
        else SetStatus("Couldn't sign in: " + (_music.SpotifyError ?? "cancelled"), bad: true);
    }

    private void SetBusy(bool busy)
    {
        if (_signInBtn != null) { Enable(_signInBtn, !busy); ((TextBlock)_signInBtn.Child).Text = busy ? "Waiting for Spotify…" : "Sign in with Spotify"; }
        if (_cancelBtn != null) _cancelBtn.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetStatus(string text, bool bad, bool good = false)
    {
        if (_status == null) return;
        _status.Text = text;
        _status.Foreground = bad ? Solid(Color.FromRgb(0xF8, 0x71, 0x71)) : good ? Solid(Color.FromRgb(0x34, 0xD3, 0x99)) : Dim;
    }

    private void SaveSource()
    {
        SettingsStore.Current.MusicSource = _source;
        if (_clientIdValue.Trim().Length > 0) SettingsStore.Current.SpotifyClientId = _clientIdValue.Trim();
        SettingsStore.Save();
    }

    // ---- auto-continue (returning users) --------------------------------------------
    private void StartAutoContinue()
    {
        _countdown = 3;
        UpdateCountLabel();
        _auto = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _auto.Tick += (_, _) =>
        {
            _countdown--;
            if (_countdown <= 0) { _auto?.Stop(); if (!_closed) DialogResult = true; return; }
            UpdateCountLabel();
        };
        _auto.Start();
    }

    private void UpdateCountLabel()
    {
        if (_countLabel != null) _countLabel.Text = $"Continuing in {_countdown}… (click anywhere to stay on this screen)";
    }

    private void StopAutoContinue()
    {
        if (_auto == null) return;
        _auto.Stop();
        _auto = null;
        if (_countLabel != null) _countLabel.Text = "";
    }

    // ============================================================ UI helpers ====
    private static readonly SolidColorBrush Dim = Solid(Color.FromArgb(0xB3, 255, 255, 255));
    private static SolidColorBrush Solid(Color c) => new(c);

    private static TextBlock Label(string text, double size, Brush fg, FontWeight? weight = null, double top = 0) => new()
    {
        Text = text, FontSize = size, Foreground = fg, FontWeight = weight ?? FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0),
    };

    private static void Enable(Border b, bool on) { b.IsEnabled = on; b.Opacity = on ? 1 : 0.45; b.Cursor = on ? Cursors.Hand : Cursors.Arrow; }

    private static Border Button(string text, bool primary, Action click)
    {
        var b = new Border
        {
            CornerRadius = new CornerRadius(12), Padding = new Thickness(22, 11, 22, 11), Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = primary ? new LinearGradientBrush(Purple, Pink, 0) : Solid(Color.FromArgb(0x1A, 255, 255, 255)),
            BorderBrush = Solid(Color.FromArgb(0x33, 255, 255, 255)), BorderThickness = new Thickness(primary ? 0 : 1),
            Child = new TextBlock { Text = text, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center },
        };
        b.MouseEnter += (_, _) => { if (b.IsEnabled) b.Opacity = 0.88; };
        b.MouseLeave += (_, _) => { if (b.IsEnabled) b.Opacity = 1; };
        b.MouseLeftButtonUp += (_, _) => { if (b.IsEnabled) click(); };
        return b;
    }

    private Grid NavBar(Action back, string nextText, Action next)
    {
        var nav = new Grid { Margin = new Thickness(0, 24, 0, 0) };
        var b = Label("‹ Back", 12.5, Dim); b.Cursor = Cursors.Hand; b.VerticalAlignment = VerticalAlignment.Center;
        b.MouseLeftButtonUp += (_, _) => back();
        var n = Button(nextText, true, next); n.HorizontalAlignment = HorizontalAlignment.Right;
        nav.Children.Add(b); nav.Children.Add(n);
        return nav;
    }

    private static FrameworkElement Field(string hint, string value, bool password, Action<string> onChange)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(1), Margin = new Thickness(0, 6, 0, 0),
            Background = Solid(Color.FromArgb(0x14, 255, 255, 255)), BorderBrush = Solid(Color.FromArgb(0x33, 255, 255, 255)),
        };
        var grid = new Grid();
        var hintText = new TextBlock
        {
            Text = hint, FontSize = 13, Foreground = Solid(Color.FromArgb(0x66, 255, 255, 255)), IsHitTestVisible = false,
            Margin = new Thickness(13, 10, 13, 10), Visibility = value.Length == 0 ? Visibility.Visible : Visibility.Collapsed,
        };
        Control input;
        if (password)
        {
            var pb = new PasswordBox { Password = value };
            pb.PasswordChanged += (_, _) => { onChange(pb.Password); hintText.Visibility = pb.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed; };
            input = pb;
        }
        else
        {
            var tb = new TextBox { Text = value };
            tb.TextChanged += (_, _) => { onChange(tb.Text); hintText.Visibility = tb.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; };
            input = tb;
        }
        input.Background = Brushes.Transparent; input.BorderThickness = new Thickness(0); input.Foreground = Brushes.White;
        input.FontSize = 13; input.Padding = new Thickness(12, 9, 12, 9);
        if (input is TextBox t2) t2.CaretBrush = Brushes.White;
        input.GotKeyboardFocus += (_, _) => border.BorderBrush = Solid(Purple);
        input.LostKeyboardFocus += (_, _) => border.BorderBrush = Solid(Color.FromArgb(0x33, 255, 255, 255));
        grid.Children.Add(hintText);
        grid.Children.Add(input);
        border.Child = grid;
        return border;
    }

    private static FrameworkElement Toggle(string text, string? hint, bool value, Action<bool> onChange, double topMargin = 0)
    {
        var on = value;
        var row = new Grid { Margin = new Thickness(0, topMargin + 6, 0, 6), Cursor = Cursors.Hand, Background = Brushes.Transparent };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(Label(text, 13, Brushes.White));
        if (hint != null) labels.Children.Add(Label(hint, 11, Dim, top: 1));

        var knob = new Ellipse { Width = 16, Height = 16, Fill = Brushes.White, HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left, Margin = new Thickness(3, 0, 3, 0) };
        var track = new Border { Width = 40, Height = 22, CornerRadius = new CornerRadius(11), Child = knob, Background = on ? Solid(Purple) : Solid(Color.FromArgb(0x55, 0x47, 0x55, 0x69)) };
        Grid.SetColumn(track, 1);
        row.MouseLeftButtonUp += (_, _) =>
        {
            on = !on;
            knob.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            track.Background = on ? Solid(Purple) : Solid(Color.FromArgb(0x55, 0x47, 0x55, 0x69));
            onChange(on);
        };
        row.Children.Add(labels); row.Children.Add(track);
        return row;
    }
}
