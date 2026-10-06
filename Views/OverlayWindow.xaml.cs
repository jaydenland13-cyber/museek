using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Museek.Services;

namespace Museek.Views;

// ---------------------------------------------------------------- view models
public sealed class QueueItem
{
    public string Id { get; init; } = "";
    public int Index { get; init; }
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Duration { get; init; } = "";
    public string? ArtUrl { get; init; }
}

public sealed class LyricLine : INotifyPropertyChanged
{
    private bool _isCurrent;
    public double Seconds { get; init; }
    public string Text { get; init; } = "";
    public bool IsCurrent
    {
        get => _isCurrent;
        set { if (_isCurrent == value) return; _isCurrent = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Glass overlay. Free-form resizable (all edges/corners), customisable (Customize tab), and its panels can be
/// popped out into separate floating widget windows. Built to idle at ~0% CPU (see README "Performance notes").
/// </summary>
public partial class OverlayWindow : Window
{
    private const int WinMargin = 16;       // transparent margin around the card (room for the fake shadow)
    private static readonly string[] WidgetKeys = { "queue", "lyrics", "chat", "visuals" };
    private static readonly string[] AllTabKeys = { "queue", "lyrics", "chat", "visuals", "settings" };

    // ---- state ------------------------------------------------------------
    private readonly IMusicSource _spotify;     // Spotify or Windows media, via MusicHub
    private readonly Func<string, Task> _onChatMessage;
    private readonly DispatcherTimer _pollTimer = new();
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _vizTimer = new() { Interval = TimeSpan.FromMilliseconds(66) };      // ~15 FPS
    private readonly DispatcherTimer _topTimer = new() { Interval = TimeSpan.FromSeconds(15) };            // safety net only; the foreground hook does the real work
    private readonly DispatcherTimer _changeDebounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _wheelTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly ObservableCollection<QueueItem> _queue = new();
    private readonly ObservableCollection<LyricLine> _lyricLines = new();
    private readonly Dictionary<string, WidgetWindow> _widgets = new();
    private readonly Dictionary<string, bool> _sectionOpen = new();
    private readonly List<(string Hex, Border Swatch)> _accentSwatches = new(), _bgSwatches = new();
    private readonly Random _rng = new();

    private const int Bars = 32;
    private readonly ScaleTransform?[] _barScales = new ScaleTransform?[Bars];
    private readonly double[] _barLevel = new double[Bars];

    private static readonly SolidColorBrush OkBrush = Frozen(Color.FromRgb(0x34, 0xD3, 0x99));
    private static readonly SolidColorBrush BadBrush = Frozen(Color.FromRgb(0xF8, 0x71, 0x71));
    private static readonly SolidColorBrush NormalBorder = Frozen(Color.FromArgb(0x1F, 255, 255, 255));
    private static readonly LinearGradientBrush GlassBorderDark = FrozenGradient(Color.FromArgb(0x4D, 255, 255, 255), Color.FromArgb(0x0F, 255, 255, 255));
    private static readonly LinearGradientBrush GlassBorderLight = FrozenGradient(Color.FromArgb(0x30, 0, 0, 0), Color.FromArgb(0x0D, 0, 0, 0));
    private static readonly SolidColorBrush RedDot = Frozen(Color.FromArgb(0xCC, 0xEF, 0x44, 0x44));
    private static readonly SolidColorBrush YellowDot = Frozen(Color.FromArgb(0xCC, 0xEA, 0xB3, 0x08));
    private static readonly SolidColorBrush GreenDot = Frozen(Color.FromArgb(0xCC, 0x22, 0xC5, 0x5E));
    private static LinearGradientBrush FrozenGradient(Color top, Color bottom) { var b = new LinearGradientBrush(top, bottom, 90); b.Freeze(); return b; }
    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
    private static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private DragController _drag = null!;
    private IntPtr _hwnd, _lastForeground;
    private TrackInfo? _current;
    private DateTime _polledAtUtc = DateTime.UtcNow, _nextQueueFetchUtc = DateTime.MinValue, _heardUntil = DateTime.MinValue;
    private string? _lastTrackId, _shownId, _lastArtUrl, _lyricsFor, _shownRepeat;
    private bool? _shownPlaying, _shownShuffle;
    private string _tab = "queue";
    private int _lyricIdx = -1, _lastSecond = -1, _topTicks, _wheelVol = -1;
    private bool _liveDirty = true, _liked, _locked, _compact, _autoScroll = true, _userHidden, _polling;
    private bool _uiUpdating, _volDragging, _chatStarted, _micOn;
    private bool _ready;      // false while InitializeComponent is still building the XAML tree
    private bool _customizeBuilt;
    private LookPreset? _lookUndo;
    private UpdateInfo? _pendingUpdate;
    private TextBlock? _updateStatus;
    private Button? _installBtn;
    private int _tourStep = -1;
    private IntPtr _fgHook;
    private WinNative.WinEventDelegate? _fgDelegate;   // kept in a field so the GC can't collect the callback
    private bool _closing;    // main window is shutting down: keep the saved 'open' state of widgets

    private static readonly string[] AccentColors =
        { "#8B5CF6", "#06B6D4", "#EC4899", "#1DB954", "#F97316", "#3B82F6", "#EF4444", "#EAB308" };
    private static readonly string[] BgColors =
        { "#0F172A", "#000000", "#1E1B4B", "#4C0519", "#022C22", "#2E1065", "#1C1917" };
    private static readonly (string Key, string Label)[] EqPresets =
        { ("flat", "Flat"), ("bass", "Bass boost"), ("vocal", "Vocal clarity"), ("electronic", "Electronic"), ("treble", "Treble boost") };

    public event Action<TrackInfo>? TrackChanged;
    public event Action? OpenSettingsRequested;
    public event Action? MicClicked;
    public event Action? QuitRequested;
    public event Action? WelcomeRequested;
    public event Action? SignOutRequested;
    public event Action? PaletteRequested;
    public event Action<UpdateInfo>? UpdateAvailable;
    public event Action? InputsChanged;
    public event Action<string>? SourceChanged;
    public event Action<string>? EqPresetChosen;

    public bool CurrentLiked => _liked;
    public bool IsLocked => _locked;

    private static AppSettings C => SettingsStore.Current;
    /// <summary>The main window sizes itself to its content (mini view, or when the tab panel is switched off).</summary>
    private bool AutoHeight => _compact || !C.ShowTabs;
    private static bool HighContrast => C.HighContrast || SystemParameters.HighContrast;                 // our option or Windows' own
    private static bool ReduceMotion => C.ReduceMotion || !SystemParameters.ClientAreaAnimation;

    public OverlayWindow(IMusicSource spotify, Func<string, Task> onChatMessage)
    {
        InitializeComponent();
        _ready = true;
        _spotify = spotify;
        _onChatMessage = onChatMessage;

        var s = C;
        _compact = s.CompactMode;
        Topmost = s.AlwaysOnTop;
        Left = s.OverlayX; Top = s.OverlayY;

        ApplyAutomationNames();
        QueueList.ItemsSource = _queue;
        LyricsList.ItemsSource = _lyricLines;
        VizCanvas.SizeChanged += (_, _) => LayoutViz();

        // move (title bar + now-playing card) and resize (any edge/corner)
        _drag = new DragController(this, WinMargin, IsInteractive) { Locked = () => _locked, Completed = SaveGeometry };
        _drag.Attach(TitleBarBorder);
        _drag.Attach(NowPlayingBorder);
        WindowResizer.Attach(this, RootGrid, () => !AutoHeight, SaveGeometry);

        TitleBarBorder.PreviewMouseLeftButtonDown += (_, e) =>      // double-click the title bar: mini <-> full
        {
            if (e.ClickCount == 2 && e.OriginalSource is DependencyObject d && !IsInteractive(d)) { SetCompact(!_compact); e.Handled = true; }
        };
        TitleBarBorder.ContextMenu = NowPlayingBorder.ContextMenu = BuildContextMenu();
        NowPlayingBorder.MouseWheel += NowPlaying_MouseWheel;
        _wheelTimer.Tick += async (_, _) => await CommitWheelVolume();

        SourceInitialized += (_, _) =>
        {
            _hwnd = WinNative.Handle(this);
            WinNative.SetExStyle(_hwnd, WinNative.WS_EX_TOOLWINDOW | WinNative.WS_EX_NOACTIVATE, true);   // never steal focus
            EnsureOnScreen();
            ForceTopmost();
        };

        TabQueue.IsChecked = true;
        BuildEqChips();
        ApplyTheme();

        _uiTimer.Tick += (_, _) => UpdateLiveUi();
        _vizTimer.Tick += (_, _) => TickViz();
        _topTimer.Tick += (_, _) => TopTick();
        _pollTimer.Tick += async (_, _) => await PollAsync();
        _changeDebounce.Tick += (_, _) => { _changeDebounce.Stop(); _ = PollAsync(); };
        _spotify.Changed += () => Dispatcher.BeginInvoke(new Action(() => { if (!_changeDebounce.IsEnabled) _changeDebounce.Start(); }));
        _pollTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(2000, s.PollIntervalMs));
        _pollTimer.Start();

        Deactivated += (_, _) => ForceTopmost();
        IsVisibleChanged += OnVisibleChanged;
        Loaded += (_, _) =>
        {
            RestoreWidgets();
            if (!C.TourDone) Dispatcher.BeginInvoke(new Action(StartTour), DispatcherPriority.ApplicationIdle);   // first run only
        };
        Closed += (_, _) => Log.Info("Overlay window closed");

        _ = PollAsync(true);
    }

    /// <summary>Timers only run while the overlay is actually visible; widgets follow the overlay.</summary>
    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var vis = IsVisible;
        Log.Info($"Overlay visible: {vis}");
        UpdateUiTimer();
        _topTimer.IsEnabled = vis;
        if (vis) HookForeground(); else UnhookForeground();
        foreach (var w in _widgets.Values) { if (vis) w.Show(); else w.Hide(); }
        if (vis) { _liveDirty = true; _ = PollAsync(); ForceTopmost(); }
        StartStopViz();
        if (!vis) MemoryTrim.RunSoon();
    }

    // =========================================================== theming ====
    public void ApplyTheme()
    {
        var s = C;
        _uiUpdating = true;
        try
        {
            var hc = HighContrast;
            var dark = hc || !string.Equals(s.ThemeMode, "light", StringComparison.OrdinalIgnoreCase);
            var accent = hc ? Color.FromRgb(0xFF, 0xD4, 0x00) : ParseColor(s.AccentColor, Color.FromRgb(0x8B, 0x5C, 0xF6));
            var baseBg = hc ? Colors.Black : ParseColor(s.BgColorOverride, dark ? Color.FromRgb(0x0F, 0x17, 0x2A) : Color.FromRgb(0xF8, 0xFA, 0xFC));
            var fg = hc ? Colors.White : ParseColor(s.TextColorOverride, dark ? Color.FromRgb(0xF8, 0xFA, 0xFC) : Color.FromRgb(0x0F, 0x17, 0x2A));

            UpdatePanelBrush();
            SetBrush("TitleBar", dark ? Color.FromArgb(0x66, baseBg.R, baseBg.G, baseBg.B) : Color.FromArgb(0x14, 0, 0, 0));
            SetBrush("FooterBar", dark ? Color.FromArgb(0xB3, baseBg.R, baseBg.G, baseBg.B) : Color.FromArgb(0x1A, 0, 0, 0));
            SetBrush("Card", dark ? Color.FromArgb(0x0D, 255, 255, 255) : Color.FromArgb(0x0A, 0, 0, 0));
            SetBrush("CardBorder", dark ? Color.FromArgb(0x14, 255, 255, 255) : Color.FromArgb(0x1A, 0, 0, 0));
            SetBrush("Fg", fg);
            SetBrush("FgDim", Blend(fg, baseBg, 0.20));
            SetBrush("FgFaint", Blend(fg, baseBg, 0.42));
            SetBrush("Accent", accent);
            SetBrush("AccentSoft", Color.FromArgb(0x33, accent.R, accent.G, accent.B));
            if (hc)                                                   // maximum contrast everywhere
            {
                SetBrush("Card", Color.FromArgb(0x26, 255, 255, 255));
                SetBrush("CardBorder", Color.FromArgb(0xAA, 255, 255, 255));
                SetBrush("FgDim", Color.FromRgb(0xF2, 0xF2, 0xF2));
                SetBrush("FgFaint", Color.FromRgb(0xD9, 0xD9, 0xD9));
                SetBrush("TitleBar", Color.FromRgb(0x10, 0x10, 0x10));
                SetBrush("FooterBar", Color.FromRgb(0x10, 0x10, 0x10));
            }
            Resources["AccentColor"] = accent;
            Resources["PurpleCyan"] = new LinearGradientBrush(accent, Color.FromRgb(0x06, 0xB6, 0xD4), 0);
            Resources["PurplePink"] = new LinearGradientBrush(accent, Color.FromRgb(0xEC, 0x48, 0x99), 45);
            ApplyLookResources(hc, accent, fg);

            var family = s.EffectiveFont;
            if (FontFamily.Source != family) FontFamily = new FontFamily(family);

            ApplyCorners();
            var zoom = Math.Clamp(s.UiScale, 0.7, 1.6);               // text & interface size
            ContentRoot.LayoutTransform = Math.Abs(zoom - 1) < 0.005 ? Transform.Identity : new ScaleTransform(zoom, zoom);
            foreach (var wd in _widgets.Values) wd.SetZoom(zoom);
            ProgressHost.Height = Math.Max(3, s.ProgressBarHeight);
            ProgressHandle.Margin = new Thickness(-7, -(14 - ProgressHost.Height) / 2, 0, 0);
            GlowLayer.Visibility = Vis(s.AmbientGlow && !hc);

            Width = Math.Max(MinWidth, s.OverlayWidth);
            _pollTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(2000, s.PollIntervalMs));

            ApplyLayout();
            UpdateLockPinVisuals();
            UpdateLikeButton();
            SetMicState(_micOn, s.PushToTalkEnabled);
            ReapplyPlaybackBrushes();
            RefreshSwatchRings();
        }
        finally { _uiUpdating = false; }
    }

    /// <summary>Everything a look controls beyond plain colours: play-button style, radii, title-bar dots, progress shape, spacing.</summary>
    private void ApplyLookResources(bool hc, Color accent, Color fg)
    {
        var s = C;
        var accentBrush = new SolidColorBrush(accent);
        var ink = new SolidColorBrush(Luminance(accent) > 0.62 ? Colors.Black : Colors.White);   // readable glyph on any accent
        Brush fill, glyph, border = Brushes.Transparent;
        var borderThickness = new Thickness(0);
        double glow, radius = 26;

        switch (hc ? "solid" : s.PlayStyle)
        {
            case "solid":   fill = accentBrush; glyph = ink; glow = 0; break;
            case "square":  fill = accentBrush; glyph = ink; glow = 0; radius = 15; break;
            case "outline": fill = Brushes.Transparent; glyph = accentBrush; border = accentBrush; borderThickness = new Thickness(2); glow = 0.30; break;
            case "ghost":   fill = Brushes.Transparent; glyph = new SolidColorBrush(fg); glow = 0; break;
            default:        fill = (Brush)Resources["PurplePink"]; glyph = Brushes.White; glow = 0.55; break;
        }
        Resources["PlayFill"] = fill;
        Resources["PlayGlyph"] = glyph;
        Resources["PlayBorder"] = border;
        Resources["PlayBorderThickness"] = borderThickness;
        Resources["PlayGlowOpacity"] = glow;
        Resources["PlayCornerRadius"] = new CornerRadius(radius);
        Resources["CardRadius"] = new CornerRadius(Math.Max(0, s.CardRadius));
        Resources["ControlRadius"] = new CornerRadius(Math.Max(0, s.ControlRadius));
        ArtBorder.CornerRadius = new CornerRadius(Math.Min(s.CardRadius + 4, 30));

        var mono = s.DotStyle == "mono" && !hc;
        var size = mono ? 10.0 : 12.0;
        foreach (var d in new[] { DotClose, DotCompact, DotExpand }) { d.Width = size; d.Height = size; }
        var monoBrush = new SolidColorBrush(Color.FromArgb(0x99, fg.R, fg.G, fg.B));
        DotClose.Fill = mono ? monoBrush : RedDot;
        DotCompact.Fill = mono ? monoBrush : YellowDot;
        DotExpand.Fill = mono ? monoBrush : GreenDot;

        var pr = s.ProgressRound ? Math.Max(1.5, s.ProgressBarHeight / 2.0) : 1.0;
        ProgressTrack.CornerRadius = new CornerRadius(pr);
        ProgressFill.CornerRadius = new CornerRadius(pr);

        ApplyDensity();
    }

    private void ApplyDensity()
    {
        var compact = C.Density == "compact";
        ArtHost.Width = ArtHost.Height = compact ? 64 : 84;
        ArtHost.Margin = new Thickness(0, 0, compact ? 12 : 16, 0);
        NowPlayingBorder.Padding = compact ? new Thickness(16, 12, 16, 8) : new Thickness(20, 18, 20, 12);
        TitleBarBorder.Height = compact ? 32 : 40;
        TabContent.Margin = compact ? new Thickness(12, 10, 12, 8) : new Thickness(16, 14, 16, 12);
        TitleText.FontSize = compact ? 16 : 18;
        ArtistText.FontSize = compact ? 12.5 : 13.5;
    }

    /// <summary>Applies a whole look (colours, shapes, spacing). The previous look is kept for "Undo".</summary>
    public void ApplyLook(LookPreset p)
    {
        _lookUndo = Looks.Capture(C);
        Looks.Apply(C, p);
        SettingsStore.Save();
        ApplyTheme();
        LayoutViz();
        Dispatcher.BeginInvoke(new Action(InvalidateCustomize));
        ShowHeard($"Look: {p.Name}");
    }

    private void UndoLook()
    {
        if (_lookUndo == null) return;
        var p = _lookUndo;
        _lookUndo = null;
        Looks.Apply(C, p);
        SettingsStore.Save();
        ApplyTheme();
        LayoutViz();
        Dispatcher.BeginInvoke(new Action(InvalidateCustomize));
    }

    private void UpdatePanelBrush()
    {
        var s = C;
        var hc = HighContrast;
        var dark = hc || !string.Equals(s.ThemeMode, "light", StringComparison.OrdinalIgnoreCase);
        var baseBg = hc ? Colors.Black : ParseColor(s.BgColorOverride, dark ? Color.FromRgb(0x0F, 0x17, 0x2A) : Color.FromRgb(0xF8, 0xFA, 0xFC));
        var alpha = hc ? 1.0 : s.BackgroundMode == "transparent" ? 0.2 : Math.Clamp(s.OverlayOpacity, 0.2, 1.0);
        SetBrush("Panel", Color.FromArgb((byte)(alpha * 255), baseBg.R, baseBg.G, baseBg.B));
    }

    private void ApplyCorners()
    {
        var s = C;
        var r = Math.Max(0, s.CornerRadius);
        PanelBorder.CornerRadius = new CornerRadius(r);
        ShadowCaster.CornerRadius = new CornerRadius(r + 1);
        ShadowOuter.CornerRadius = new CornerRadius(r + 4);
        GlowLayer.CornerRadius = new CornerRadius(r);
        TitleBarBorder.CornerRadius = new CornerRadius(r, r, 0, 0);
        FooterBorder.CornerRadius = new CornerRadius(0, 0, r, r);
        var hc = HighContrast;
        PanelBorder.BorderThickness = new Thickness(Math.Max(hc ? 2 : 0, s.BorderWidth));
        var light = string.Equals(s.ThemeMode, "light", StringComparison.OrdinalIgnoreCase);
        PanelBorder.BorderBrush = hc ? Brushes.White
            : s.BorderAccent ? new SolidColorBrush(Color.FromArgb(0xCC, ParseColor(s.AccentColor, Colors.White).R, ParseColor(s.AccentColor, Colors.White).G, ParseColor(s.AccentColor, Colors.White).B))
            : light ? GlassBorderLight : GlassBorderDark;
    }

    /// <summary>Applies every "show / hide this part" option and the window sizing mode.</summary>
    private void ApplyLayout()
    {
        var s = C;
        ArtHost.Visibility = Vis(s.ShowArt);
        NowPlayingPill.Visibility = Vis(s.ShowNowPlayingLabel);
        ArtistText.Visibility = Vis(s.ShowArtistName);
        AlbumText.Visibility = Vis(s.ShowAlbum);
        ProgressBlock.Visibility = Vis(s.ShowProgress);
        TimeRow.Visibility = Vis(s.ShowTimeText);
        ControlsGrid.Visibility = Vis(s.ShowControls);
        var caps = _spotify.Caps;
        ShuffleBtn.Visibility = s.ShowShuffleButton && caps.HasFlag(MusicCaps.ShuffleRepeat) ? Visibility.Visible : Visibility.Hidden;   // Hidden keeps play centred
        RepeatBtn.Visibility = s.ShowRepeatButton && caps.HasFlag(MusicCaps.ShuffleRepeat) ? Visibility.Visible : Visibility.Hidden;
        LikeBtn.Visibility = Vis(s.ShowLikeButton && caps.HasFlag(MusicCaps.Like));
        QueueAddRow.Visibility = Vis(caps.HasFlag(MusicCaps.Search));
        QueueHeaderRow.Visibility = Vis(caps.HasFlag(MusicCaps.Queue));
        VolumeCard.Visibility = Vis(caps.HasFlag(MusicCaps.Volume));
        QueueEmpty.Text = caps.HasFlag(MusicCaps.Queue)
            ? "The queue is empty."
            : $"{_spotify.Name} doesn't share its queue with Museek.\nUse your music app to see what's next.";
        DotClose.Visibility = Vis(s.ShowCloseButton);
        LockBtn.Visibility = Vis(s.ShowLockButton);

        var tabsOn = !_compact && s.ShowTabs;
        TabsHost.Visibility = Vis(tabsOn);
        TabContent.Visibility = Vis(tabsOn);
        FooterBorder.Visibility = Vis(!_compact && s.ShowFooter);

        RefreshTabs();
        UpdateSizing();
        StartStopViz();
        UpdateUiTimer();
    }

    private void UpdateSizing()
    {
        if (AutoHeight)
        {
            MinHeight = 0;
            SizeToContent = SizeToContent.Height;
        }
        else
        {
            MinHeight = 470;
            SizeToContent = SizeToContent.Manual;
            Height = Math.Max(MinHeight, C.OverlayHeight);
        }
    }

    private void SaveGeometry()
    {
        var s = C;
        s.OverlayX = (int)Left; s.OverlayY = (int)Top; s.OverlayWidth = (int)Width;
        if (!AutoHeight) s.OverlayHeight = (int)Height;
        SettingsStore.Save();
    }

    private void ReapplyPlaybackBrushes()
    {
        if (_shownShuffle is bool sh) ShuffleBtn.Foreground = sh ? GetBrush("Accent") : GetBrush("FgDim");
        if (_shownRepeat is string rp) RepeatBtn.Foreground = rp != "off" ? GetBrush("Accent") : GetBrush("FgDim");
    }

    private void SetBrush(string key, Color c) => Resources[key] = new SolidColorBrush(c);
    private Brush GetBrush(string key) => (Brush)Resources[key];
    private static Visibility Vis(bool v) => v ? Visibility.Visible : Visibility.Collapsed;

    private static Color ParseColor(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try { return (Color)ColorConverter.ConvertFromString(hex); } catch { return fallback; }
    }

    private static Color Blend(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private void UpdateLockPinVisuals()
    {
        LockBtn.Content = _locked ? "\uE72E" : "\uE785";
        LockBtn.Foreground = _locked ? GetBrush("Accent") : GetBrush("FgDim");
        PinBtn.Foreground = Topmost ? GetBrush("Accent") : GetBrush("FgDim");
    }

    // ============================================================== tabs ====
    private RadioButton TabFor(string key) => key switch
    {
        "queue" => TabQueue, "lyrics" => TabLyrics, "chat" => TabChat, "visuals" => TabVisuals, _ => TabSettings,
    };

    private FrameworkElement PanelFor(string key) => key switch
    {
        "queue" => PanelQueue, "lyrics" => PanelLyrics, "chat" => PanelChat, "visuals" => PanelVisuals, _ => PanelSettings,
    };

    private static string TitleFor(string key) => key switch
    {
        "queue" => "Up Next", "lyrics" => "Lyrics", "chat" => "Chat", "visuals" => "Visuals & sound", _ => "Customize",
    };

    private bool TabAvailable(string key)
    {
        if (_widgets.ContainsKey(key)) return false;                  // popped out: no tab
        return key switch { "lyrics" => C.LyricsEnabled, "chat" => C.ChatbotEnabled, _ => true };
    }

    private void RefreshTabs()
    {
        var n = 0;
        foreach (var k in AllTabKeys)
        {
            var ok = TabAvailable(k);
            TabFor(k).Visibility = Vis(ok);
            if (ok) n++;
        }
        TabsRow.Columns = Math.Max(1, n);
        if (!TabAvailable(_tab)) TabFor(AllTabKeys.First(TabAvailable)).IsChecked = true;   // -> Tab_Checked
        else SetPanelVisibility();
    }

    private void SetPanelVisibility()
    {
        foreach (var k in AllTabKeys)
            PanelFor(k).Visibility = _widgets.ContainsKey(k) ? Visibility.Visible : Vis(_tab == k);
    }

    /// <summary>Is this panel actually on screen right now (in the overlay's tab or in its own widget)?</summary>
    private bool PanelShown(string key) =>
        IsVisible && (_widgets.ContainsKey(key) || (_tab == key && TabContent.Visibility == Visibility.Visible));

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not RadioButton rb) return;
        _tab = (string)rb.Tag;
        SetPanelVisibility();
        StartStopViz();
        _liveDirty = true;
        if (_tab == "lyrics") { EnsureLyrics(); ScrollLyricTo(_lyricIdx); }
        if (_tab == "settings" && !_customizeBuilt) BuildCustomize();       // built on first open, not at startup
        if (_tab == "queue" && IsVisible) _ = PollAsync(true);          // fetch the queue only when it is shown
    }

    // ========================================================== polling =====
    private async Task PollAsync(bool forceQueue = false)
    {
        if (_polling) return;
        _polling = true;
        try
        {
            var cur = await _spotify.GetCurrentTrackAsync();

            // The queue is a second API call: only fetch it when an Up Next panel is actually showing.
            List<TrackInfo>? queue = null;
            if (cur != null && PanelShown("queue") && _spotify.Caps.HasFlag(MusicCaps.Queue) &&
                (forceQueue || cur.Id != _shownId || DateTime.UtcNow >= _nextQueueFetchUtc))
            {
                queue = await _spotify.GetQueueAsync(8);
                _nextQueueFetchUtc = DateTime.UtcNow.AddSeconds(30);
            }

            _polledAtUtc = DateTime.UtcNow;
            Render(cur, cur == null ? new List<TrackInfo>() : queue);
        }
        finally
        {
            _polling = false;
            _pollTimer.Interval = NextPollInterval();
        }
    }

    /// <summary>Fast while playing & visible, slow when paused/idle/hidden; wakes right at track end.</summary>
    private TimeSpan NextPollInterval()
    {
        var baseMs = _spotify.IsLocal ? 4000 : Math.Max(2000, C.PollIntervalMs);       // Windows media pushes change events; this is just a safety net
        if (!IsVisible || _current == null || !_current.IsPlaying)
            return TimeSpan.FromMilliseconds(_spotify.IsLocal ? 10000 : Math.Max(baseMs, 8000));

        var pos = _current.ProgressMs + (int)(DateTime.UtcNow - _polledAtUtc).TotalMilliseconds;
        var remaining = _current.DurationMs - pos;
        if (remaining > 0 && remaining < baseMs) return TimeSpan.FromMilliseconds(remaining + 400);
        return TimeSpan.FromMilliseconds(baseMs);
    }

    /// <summary>Refresh shortly after a control action so the UI reflects it.</summary>
    public async void RefreshSoon()
    {
        await Task.Delay(400);
        await PollAsync(true);
    }

    private void Render(TrackInfo? cur, List<TrackInfo>? queue)
    {
        _current = cur;
        _liveDirty = true;
        var s = C;

        if (DateTime.UtcNow >= _heardUntil)
        {
            var text = _spotify.StatusText;
            if (StatusText.Text != text) StatusText.Text = text;
            var dot = _spotify.IsConnected ? OkBrush : BadBrush;
            if (!ReferenceEquals(StatusDot.Fill, dot)) StatusDot.Fill = dot;
        }

        if (cur == null)
        {
            if (_shownId != null || _shownPlaying != null)
            {
                _shownId = null; _shownPlaying = null; _shownShuffle = null; _shownRepeat = null;
                TitleText.Text = "Nothing playing";
                ArtistText.Text = ""; AlbumText.Text = "";
                CurTimeText.Text = "0:00"; DurTimeText.Text = "0:00";
                ProgressScale.ScaleX = 0; HandleShift.X = 0;
                PlayBtn.Content = "\uE768";
                ArtBorder.Background = new SolidColorBrush(Color.FromArgb(0x33, 0x80, 0x80, 0x80));
                _lastArtUrl = null;
                _lyricLines.Clear();
                StartStopViz();
            }
            if (queue != null) SyncQueue(queue);
            UpdateUiTimer();
            if (s.AutoHideWhenIdle && Visibility == Visibility.Visible) Hide();
            return;
        }

        if (s.AutoHideWhenIdle && !_userHidden && Visibility != Visibility.Visible) Show();

        // ---- only touch the UI for things that actually changed ----
        if (cur.Id != _shownId)
        {
            _shownId = cur.Id;
            TitleText.Text = cur.Name;
            ArtistText.Text = cur.Artist;
            AlbumText.Text = cur.Album;
            DurTimeText.Text = MsToMmss(cur.DurationMs);
            _lastSecond = -1;

            var prev = _lastTrackId;
            _lastTrackId = cur.Id;
            _lyricLines.Clear();
            _lyricIdx = -1;
            _lyricsFor = null;
            LyricsStatus.Text = "Nothing to show yet.";
            LyricsStatus.Visibility = Visibility.Visible;
            if (_spotify.Caps.HasFlag(MusicCaps.Like)) _ = CheckLikedAsync(cur.Id);
            else { _liked = false; UpdateLikeButton(); }
            if (PanelShown("lyrics")) EnsureLyrics();
            if (prev != null && prev != cur.Id) TrackChanged?.Invoke(cur);
        }

        if (_shownPlaying != cur.IsPlaying)
        {
            _shownPlaying = cur.IsPlaying;
            PlayBtn.Content = cur.IsPlaying ? "\uE769" : "\uE768";
            StartStopViz();
        }
        if (_shownShuffle != cur.ShuffleState)
        {
            _shownShuffle = cur.ShuffleState;
            ShuffleBtn.Foreground = cur.ShuffleState ? GetBrush("Accent") : GetBrush("FgDim");
        }
        if (_shownRepeat != cur.RepeatState)
        {
            _shownRepeat = cur.RepeatState;
            RepeatBtn.Foreground = cur.RepeatState != "off" ? GetBrush("Accent") : GetBrush("FgDim");
            RepeatBtn.Content = cur.RepeatState == "track" ? "\uE8ED" : "\uE8EE";
        }

        if (!_volDragging && _wheelVol < 0 && cur.VolumePercent is int vol && (int)VolumeSlider.Value != vol)
        {
            _uiUpdating = true;
            VolumeSlider.Value = vol;
            VolVal.Text = $"{vol}%";
            _uiUpdating = false;
        }

        if (queue != null) SyncQueue(queue);

        if (cur.AlbumArtUrl != null && cur.AlbumArtUrl != _lastArtUrl)
        {
            _lastArtUrl = cur.AlbumArtUrl;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(cur.AlbumArtUrl);
                bmp.DecodePixelWidth = 168;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                ArtBorder.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
                if (bmp.IsDownloading) bmp.DownloadCompleted += (_, _) => ApplyAmbient(bmp);
                else ApplyAmbient(bmp);
            }
            catch (Exception ex) { Log.Warn($"Album art failed: {ex.Message}"); }
        }

        else if (cur.AlbumArtUrl == null && _lastArtUrl != null)       // new song with no artwork: don't keep the old cover
        {
            _lastArtUrl = null;
            ArtBorder.Background = new SolidColorBrush(Color.FromArgb(0x33, 0x80, 0x80, 0x80));
        }

        UpdateLiveUi();
        UpdateUiTimer();
    }

    private void SyncQueue(List<TrackInfo> tracks)
    {
        var same = tracks.Count == _queue.Count && tracks.Select(t => t.Id).SequenceEqual(_queue.Select(q => q.Id));
        if (!same)
        {
            _queue.Clear();
            var i = 1;
            foreach (var t in tracks)
                _queue.Add(new QueueItem
                {
                    Id = t.Id, Index = i++, Title = t.Name, Artist = t.Artist,
                    Duration = MsToMmss(t.DurationMs), ArtUrl = t.AlbumArtUrl,
                });
        }
        QueueEmpty.Visibility = Vis(_queue.Count == 0);
    }

    /// <summary>Smooth progress + lyric highlighting between polls (render-only: no layout passes).</summary>
    private void UpdateLiveUi()
    {
        if (!IsVisible || _current == null || _current.DurationMs <= 0) return;
        if (!_current.IsPlaying && !_liveDirty) return;               // paused and already drawn
        _liveDirty = false;

        var pos = _current.ProgressMs;
        if (_current.IsPlaying) pos += (int)(DateTime.UtcNow - _polledAtUtc).TotalMilliseconds;
        pos = Math.Clamp(pos, 0, _current.DurationMs);

        var frac = (double)pos / _current.DurationMs;
        ProgressScale.ScaleX = frac;
        HandleShift.X = ProgressHost.ActualWidth * frac;

        var sec = pos / 1000;
        if (sec != _lastSecond) { _lastSecond = sec; CurTimeText.Text = MsToMmss(pos); }

        if (_lyricLines.Count > 0 && PanelShown("lyrics"))
        {
            var seconds = pos / 1000.0;
            var idx = -1;
            for (var i = 0; i < _lyricLines.Count; i++)
            {
                if (_lyricLines[i].Seconds <= seconds) idx = i; else break;
            }
            if (idx != _lyricIdx)
            {
                if (_lyricIdx >= 0 && _lyricIdx < _lyricLines.Count) _lyricLines[_lyricIdx].IsCurrent = false;
                _lyricIdx = idx;
                if (idx >= 0) { _lyricLines[idx].IsCurrent = true; ScrollLyricTo(idx); }
            }
        }
    }

    // ======================================================== ambient glow ===
    private void ApplyAmbient(BitmapSource src)
    {
        if (!C.AmbientGlow) return;
        try
        {
            var fmt = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            var small = new TransformedBitmap(fmt, new ScaleTransform(16.0 / fmt.PixelWidth, 16.0 / fmt.PixelHeight));
            int w = small.PixelWidth, h = small.PixelHeight, stride = w * 4;
            var px = new byte[stride * h];
            small.CopyPixels(px, stride, 0);

            double rs = 0, gs = 0, bs = 0, ws = 0;
            for (var i = 0; i < px.Length; i += 4)
            {
                double b = px[i], g = px[i + 1], r = px[i + 2];
                var max = Math.Max(r, Math.Max(g, b)); var min = Math.Min(r, Math.Min(g, b));
                var weight = 0.1 + (max == 0 ? 0 : (max - min) / max);     // favour colourful pixels over grey
                rs += r * weight; gs += g * weight; bs += b * weight; ws += weight;
            }
            if (ws <= 0) return;
            var c = Vivid((byte)(rs / ws), (byte)(gs / ws), (byte)(bs / ws));

            var dur = TimeSpan.FromMilliseconds(600);
            if (ReduceMotion)
            {
                GlowCenter.BeginAnimation(GradientStop.ColorProperty, null);
                GlowEdge.BeginAnimation(GradientStop.ColorProperty, null);
                GlowCenter.Color = Color.FromArgb(0x70, c.R, c.G, c.B);
                GlowEdge.Color = Color.FromArgb(0x00, c.R, c.G, c.B);
            }
            else
            {
                GlowCenter.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(Color.FromArgb(0x70, c.R, c.G, c.B), dur));
                GlowEdge.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(Color.FromArgb(0x00, c.R, c.G, c.B), dur));
            }
        }
        catch (Exception ex) { Log.Warn($"Ambient glow failed: {ex.Message}"); }
    }

    private static Color Vivid(byte r8, byte g8, byte b8)
    {
        double R = r8 / 255.0, G = g8 / 255.0, B = b8 / 255.0;
        double max = Math.Max(R, Math.Max(G, B)), min = Math.Min(R, Math.Min(G, B)), d = max - min, h = 0;
        if (d > 0)
        {
            if (max == R) h = ((G - B) / d) % 6;
            else if (max == G) h = (B - R) / d + 2;
            else h = (R - G) / d + 4;
            h *= 60; if (h < 0) h += 360;
        }
        var s = Math.Max(max == 0 ? 0 : d / max, 0.55);
        var v = Math.Max(max, 0.75);
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c, r1, g1, b1;
        switch ((int)(h / 60))
        {
            case 0: r1 = c; g1 = x; b1 = 0; break;
            case 1: r1 = x; g1 = c; b1 = 0; break;
            case 2: r1 = 0; g1 = c; b1 = x; break;
            case 3: r1 = 0; g1 = x; b1 = c; break;
            case 4: r1 = x; g1 = 0; b1 = c; break;
            default: r1 = c; g1 = 0; b1 = x; break;
        }
        return Color.FromRgb((byte)((r1 + m) * 255), (byte)((g1 + m) * 255), (byte)((b1 + m) * 255));
    }

    /// <summary>
    /// The progress timer exists only while a song is playing and the overlay is visible, and ticks no faster
    /// than the bar can show a change (about one device pixel) — 0.5–1 s. When paused or hidden it never wakes.
    /// </summary>
    private void UpdateUiTimer()
    {
        var want = IsVisible && _current?.IsPlaying == true;
        if (want)
        {
            var px = ProgressBlock.Visibility == Visibility.Visible ? ProgressHost.ActualWidth * VisualTreeHelper.GetDpi(this).DpiScaleX : 0;
            var ms = _current != null && px > 0 ? _current.DurationMs / px : 1000;
            _uiTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(ms, 500, 1000));
        }
        if (_uiTimer.IsEnabled != want) _uiTimer.IsEnabled = want;
    }

    // ===================================================== always-on-top (event driven) ===
    private void HookForeground()
    {
        if (_fgHook != IntPtr.Zero) return;
        _fgDelegate = (_, _, _, _, _, _, _) => OnForegroundChanged();
        _fgHook = WinNative.SetWinEventHook(WinNative.EVENT_SYSTEM_FOREGROUND, WinNative.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _fgDelegate, 0, 0, WinNative.WINEVENT_OUTOFCONTEXT | WinNative.WINEVENT_SKIPOWNPROCESS);
    }

    private void UnhookForeground()
    {
        if (_fgHook == IntPtr.Zero) return;
        WinNative.UnhookWinEvent(_fgHook);
        _fgHook = IntPtr.Zero;
    }

    /// <summary>Another app came to the front: re-assert "always on top". Zero polling, zero cost while nothing changes.</summary>
    private void OnForegroundChanged()
    {
        if (!IsVisible || !Topmost || _drag.IsDragging) return;
        ForceTopmost();
        foreach (var w in _widgets.Values) w.ForceTopmost();
    }

    // ============================================================ visuals ===
    // Bars are full-height rectangles scaled with a RenderTransform => no layout work per frame.
    private void LayoutViz()
    {
        VizCanvas.Children.Clear();
        var w = VizCanvas.ActualWidth;
        if (w < 20) return;

        var accent = ((SolidColorBrush)GetBrush("Accent")).Color;
        var brush = new LinearGradientBrush(Color.FromRgb(0x06, 0xB6, 0xD4), accent, 90);
        brush.Freeze();

        const double gap = 3;
        var bw = (w - gap * (Bars - 1)) / Bars;
        for (var i = 0; i < Bars; i++)
        {
            var scale = new ScaleTransform(1, 0.05);
            var r = new Rectangle
            {
                Width = bw, Height = VizCanvas.Height, RadiusX = 2, RadiusY = 2, Fill = brush,
                RenderTransformOrigin = new Point(0.5, 1), RenderTransform = scale,
            };
            Canvas.SetLeft(r, i * (bw + gap));
            Canvas.SetTop(r, 0);
            VizCanvas.Children.Add(r);
            _barScales[i] = scale;
            _barLevel[i] = 0.05;
        }
    }

    /// <summary>The 20 FPS visualizer timer only exists while the Visuals panel is shown and music plays.</summary>
    private void StartStopViz()
    {
        if (PanelShown("visuals") && !ReduceMotion) { if (!_vizTimer.IsEnabled) _vizTimer.Start(); }
        else _vizTimer.Stop();
    }

    private void TickViz()
    {
        if (!PanelShown("visuals")) { _vizTimer.Stop(); return; }
        var playing = _current?.IsPlaying == true;
        var moving = false;
        for (var i = 0; i < Bars; i++)
        {
            if (_barScales[i] is not ScaleTransform sc) continue;
            var target = playing ? 0.12 + _rng.NextDouble() * 0.85 : 0.05;
            _barLevel[i] += (target - _barLevel[i]) * 0.4;
            sc.ScaleY = Math.Max(0.05, _barLevel[i]);
            if (Math.Abs(target - _barLevel[i]) > 0.02) moving = true;
        }
        if (!playing && !moving) _vizTimer.Stop();              // settled: stop burning CPU
    }

    // ============================================================ lyrics ====
    public void ToggleLyricsPanel()
    {
        if (!C.LyricsEnabled) return;
        if (Visibility != Visibility.Visible) ShowByUser();
        if (_widgets.ContainsKey("lyrics")) { Dock("lyrics"); return; }       // popped out: toggle = put it away
        if (_tab == "lyrics") TabQueue.IsChecked = true;
        else { ShowTabsNow(); TabLyrics.IsChecked = true; }
    }

    private void EnsureLyrics()
    {
        if (_current == null) { LyricsStatus.Text = "Nothing playing."; return; }
        if (_lyricsFor == _current.Id) return;
        _ = FetchLyricsAsync(_current);
    }

    private async Task FetchLyricsAsync(TrackInfo track)
    {
        _lyricsFor = track.Id;
        _lyricLines.Clear();
        _lyricIdx = -1;
        LyricsStatus.Text = "Looking for lyrics…";
        LyricsStatus.Visibility = Visibility.Visible;

        var lines = await LyricsService.FetchSyncedAsync(track.Name, track.Artist, track.DurationMs);
        if (_current?.Id != track.Id) return;                 // track changed while fetching
        if (lines == null) { LyricsStatus.Text = "No synced lyrics found for this track."; return; }

        LyricsStatus.Visibility = Visibility.Collapsed;
        foreach (var (sec, text) in lines) _lyricLines.Add(new LyricLine { Seconds = sec, Text = text });
        _liveDirty = true;
    }

    private void ScrollLyricTo(int i)
    {
        if (!_autoScroll || i < 0) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            try
            {
                if (LyricsList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement c) return;
                var y = c.TransformToAncestor(LyricsScroll).Transform(new Point(0, 0)).Y;
                LyricsScroll.ScrollToVerticalOffset(LyricsScroll.VerticalOffset + y - LyricsScroll.ViewportHeight / 2 + c.ActualHeight / 2);
            }
            catch { /* panel hidden / not laid out yet */ }
        }));
    }

    private void AutoScroll_Click(object s, RoutedEventArgs e)
    {
        _autoScroll = !_autoScroll;
        AutoScrollBtn.Content = _autoScroll ? "Auto-scroll: ON" : "Auto-scroll: OFF";
        if (_autoScroll) ScrollLyricTo(_lyricIdx);
    }

    private async void Lyric_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LyricLine line } || _current == null) return;
        var ms = (int)(line.Seconds * 1000);
        _current = _current with { ProgressMs = ms };
        _polledAtUtc = DateTime.UtcNow;
        _liveDirty = true;
        await _spotify.SeekToAsync(ms);
    }

    // ============================================================= likes ====
    private async Task CheckLikedAsync(string trackId)
    {
        var saved = await _spotify.IsTrackSavedAsync(trackId);
        if (_current?.Id == trackId) { _liked = saved; UpdateLikeButton(); }
    }

    public void SetLiked(bool liked) { _liked = liked; UpdateLikeButton(); }

    public void UpdateLikeButton()
    {
        LikeBtn.Content = _liked ? "\uEB52" : "\uEB51";               // filled / outline heart
        LikeBtn.Foreground = _liked ? GetBrush("Pink") : GetBrush("FgDim");
    }

    // ====================================================== public actions ===
    public void ToggleVisibility()
    {
        if (Visibility == Visibility.Visible) { _userHidden = true; Hide(); }
        else { _userHidden = false; Show(); }
    }

    public new void Show() { base.Show(); }
    public new void Hide() { base.Hide(); }
    public void ShowByUser() { _userHidden = false; base.Show(); }
    public void HideByUser() { _userHidden = true; base.Hide(); }

    public void ToggleLock()
    {
        _locked = !_locked;
        WinNative.SetExStyle(_hwnd, WinNative.WS_EX_TRANSPARENT, _locked);     // whole window becomes click-through
        foreach (var w in _widgets.Values) w.SetClickThrough(_locked);
        UpdateLockPinVisuals();
    }

    public void SetAlwaysOnTop(bool on)
    {
        Topmost = on;
        foreach (var w in _widgets.Values) w.Topmost = on;
        C.AlwaysOnTop = on;
        SettingsStore.Save();
        ForceTopmost();
        UpdateLockPinVisuals();
    }

    public void ToggleChatPanel()
    {
        if (!C.ChatbotEnabled) return;
        if (Visibility != Visibility.Visible) ShowByUser();
        if (_widgets.ContainsKey("chat")) { FocusInput(ChatInput); return; }
        if (_tab == "chat") { TabQueue.IsChecked = true; return; }
        ShowTabsNow();
        TabChat.IsChecked = true;
        FocusInput(ChatInput);
    }

    /// <summary>Brings the chat panel into view (unlike ToggleChatPanel this never hides it).</summary>
    public void ShowChat()
    {
        if (!C.ChatbotEnabled) return;
        ShowByUser();
        if (_widgets.ContainsKey("chat")) return;
        ShowTabsNow();
        TabChat.IsChecked = true;
    }

    /// <summary>Thread-safe append to the chat log.</summary>
    public void AppendChat(string text)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_chatStarted) { ChatLog.Text = ""; _chatStarted = true; }
            ChatLog.Text += (ChatLog.Text.Length > 0 ? "\n\n" : "") + text;
            ChatScroll.ScrollToEnd();
        });
    }

    /// <summary>Mini view: just the now-playing card + controls (no tabs, no footer).</summary>
    public void SetCompact(bool compact)
    {
        _compact = compact;
        C.CompactMode = compact;
        SettingsStore.Save();
        ApplyLayout();
    }

    /// <summary>Make sure the tab area is on screen (used by hotkeys / links that need a tab).</summary>
    private void ShowTabsNow()
    {
        if (!_compact && C.ShowTabs) return;
        _compact = false; C.CompactMode = false; C.ShowTabs = true;
        SettingsStore.Save();
        ApplyLayout();
    }

    public void OpenCustomize()
    {
        ShowByUser();
        ShowTabsNow();
        TabSettings.IsChecked = true;
    }

    // ====================================================== voice / eq =====
    public void SetMicState(bool on, bool pushToTalk)
    {
        _micOn = on;
        MicBtn.Foreground = on ? GetBrush("Accent") : GetBrush("FgDim");
        MicBtn.ToolTip = pushToTalk ? "Push-to-talk: click (or use the hotkey), then speak a command"
                       : on ? "Listening for the wake word — click to turn voice off"
                       : "Voice is off — click to turn on";
    }

    /// <summary>Shows a short message in the status bar (e.g. what the mic heard).</summary>
    public void ShowHeard(string text)
    {
        _heardUntil = DateTime.UtcNow.AddSeconds(4);
        StatusText.Text = text;
        StatusDot.Fill = GetBrush("Accent");
    }

    public void SetEqStatus(string message) => EqStatus.Text = message;

    private void Mic_Click(object s, RoutedEventArgs e) => MicClicked?.Invoke();

    private void BuildEqChips()
    {
        EqChips.Children.Clear();
        foreach (var (key, label) in EqPresets)
        {
            var rb = new RadioButton
            {
                Content = label, Tag = key, GroupName = "eq",
                Style = (Style)FindResource("Chip"),
                IsChecked = key == C.EqPreset,
            };
            rb.Checked += (sender, _) =>
            {
                if (!_ready || _uiUpdating) return;
                var k = (string)((RadioButton)sender).Tag;
                C.EqPreset = k;
                SettingsStore.Save();
                EqPresetChosen?.Invoke(k);
            };
            EqChips.Children.Add(rb);
        }
    }

    // ======================================================= pop-out widgets ===
    private WidgetState GetWidgetState(string key)
    {
        if (!C.Widgets.TryGetValue(key, out var st)) { st = new WidgetState(); C.Widgets[key] = st; }
        return st;
    }

    private void RestoreWidgets()
    {
        foreach (var key in WidgetKeys)
            if (GetWidgetState(key).Open) PopOut(key);
    }

    /// <summary>Moves a panel out of the overlay into its own floating window.</summary>
    public void PopOut(string key)
    {
        if (_widgets.ContainsKey(key) || !WidgetKeys.Contains(key)) return;
        var panel = PanelFor(key);
        var st = GetWidgetState(key);

        if (!st.Placed)
        {
            var idx = Array.IndexOf(WidgetKeys, key);
            st.W = 380; st.H = 440;
            st.X = Left + Width + 6;
            if (st.X + st.W > SystemParameters.VirtualScreenWidth) st.X = Math.Max(0, Left - st.W - 6);   // no room on the right
            st.Y = Top + idx * 36;
            st.Placed = true;
        }

        TabContent.Children.Remove(panel);
        var w = new WidgetWindow(key, TitleFor(key), Resources, panel, st) { Topmost = Topmost };
        w.DockRequested += k => Dock(k);
        w.GeometryChanged += (k, r) =>
        {
            var g = GetWidgetState(k);
            g.X = r.X; g.Y = r.Y; g.W = r.Width; g.H = r.Height;
            SettingsStore.Save();
        };
        w.Closed += (_, _) => { if (!_closing && _widgets.Remove(key)) Reattach(key, panel); };     // closed some other way

        _widgets[key] = w;
        st.Open = true;
        SettingsStore.Save();
        panel.Visibility = Visibility.Visible;
        if (IsVisible) w.Show();
        if (_locked) w.SetClickThrough(true);

        RefreshTabs();
        StartStopViz();
        if (key == "lyrics") EnsureLyrics();
        if (key == "queue") _ = PollAsync(true);
        if (key == "visuals") LayoutViz();
    }

    /// <summary>Puts a popped-out panel back into the overlay's tab area.</summary>
    public void Dock(string key)
    {
        if (!_widgets.Remove(key, out var w)) return;
        var panel = PanelFor(key);
        w.Detach();
        w.Close();
        Reattach(key, panel);
    }

    private void Reattach(string key, FrameworkElement panel)
    {
        if (!TabContent.Children.Contains(panel)) TabContent.Children.Add(panel);
        GetWidgetState(key).Open = false;
        SettingsStore.Save();
        RefreshTabs();
        StartStopViz();
    }

    public void DockAllWidgets()
    {
        foreach (var key in _widgets.Keys.ToList()) Dock(key);
    }

    /// <summary>Called by the app after the music source changed (Spotify <-> Windows media).</summary>
    public void OnSourceChanged()
    {
        _current = null;
        _shownId = null; _shownPlaying = null; _shownShuffle = null; _shownRepeat = null;
        _lastArtUrl = null; _lyricsFor = null; _lastSecond = -1;
        _queue.Clear(); _lyricLines.Clear();
        ApplyLayout();                      // hide/show controls this source can't do
        InvalidateCustomize();
        _ = PollAsync(true);
    }

    /// <summary>After "reset to defaults": put everything back and re-read the (new) settings.</summary>
    public void ApplyAfterReset()
    {
        DockAllWidgets();
        var s = C;
        _compact = s.CompactMode;
        Topmost = s.AlwaysOnTop;
        Left = s.OverlayX; Top = s.OverlayY;
        ApplyTheme();
        InvalidateCustomize();
    }

    // ===================================== accessibility · tour · share card · updates ===
    private static string PrettyKey(string combo) =>
        string.Join(" + ", combo.Split('+').Select(x => x.Length > 0 ? char.ToUpper(x[0]) + x[1..] : x));

    private static string Hotkey(string name) => C.Hotkeys.TryGetValue(name, out var c) ? PrettyKey(c) : "(not set)";

    /// <summary>Screen-reader (Narrator) names for the icon-only controls.</summary>
    private void ApplyAutomationNames()
    {
        void N(DependencyObject d, string name) => AutomationProperties.SetName(d, name);
        N(PlayBtn, "Play or pause"); N(PrevBtn, "Previous track"); N(NextBtn, "Next track");
        N(ShuffleBtn, "Shuffle"); N(RepeatBtn, "Repeat"); N(LikeBtn, "Like song");
        N(MicBtn, "Voice commands"); N(LockBtn, "Lock position"); N(PinBtn, "Always on top");
        N(DotClose, "Hide overlay"); N(DotCompact, "Mini view"); N(DotExpand, "Full view");
        N(ProgressHost, "Song progress. Click to seek.");
        N(AddTrackInput, "Search for a song to add to the queue"); N(ChatInput, "Ask Museek a question");
    }

    /// <summary>Applies a new text & interface size, growing/shrinking the window to match so the layout stays the same.</summary>
    public void ApplyZoom(double newK)
    {
        newK = Math.Clamp(newK, 0.7, 1.6);
        var oldK = Math.Clamp(C.UiScale, 0.7, 1.6);
        if (Math.Abs(newK - oldK) < 0.005) return;
        var ratio = newK / oldK;
        C.UiScale = newK;
        C.OverlayWidth = (int)Math.Clamp(C.OverlayWidth * ratio, 360, 4000);
        if (!AutoHeight) C.OverlayHeight = (int)Math.Clamp(C.OverlayHeight * ratio, 470, 4000);
        SettingsStore.Save();
        ApplyTheme();
    }

    // ---- first-run tour -----------------------------------------------------
    private List<(string Title, string Body)> BuildTourSteps()
    {
        var full = _spotify.Caps.HasFlag(MusicCaps.Queue);
        return new List<(string, string)>
        {
            ("Welcome to Museek 🎵",
             "A little glass player that floats above everything.\n\nDrag the title bar to move it, drag any edge or corner to resize it, and double-click the title bar for mini view."),
            ("Playback",
             (full ? "Use the buttons, click the progress bar to jump around, and scroll over the song card to change the volume."
                   : "Museek follows whatever Windows says is playing, such as the Apple Music app. Use the buttons, click the progress bar to seek, and scroll over the song card for system volume.")
             + "\n\nRight-click the overlay for more: copy the song name, lock it in place, keep it on top…"),
            ("Tabs & pop-out widgets",
             "Lyrics, Chat, Visuals" + (full ? " and your Up Next queue" : "") + " live in the tabs below. Right-click → Pop out panel turns any of them into its own floating window you can put anywhere."),
            ("Shortcuts",
             $"Show / hide overlay:   {Hotkey("toggle_overlay")}\nCommand palette:   {Hotkey("command_palette")}\nPlay / pause:   {Hotkey("play_pause")}\nPush-to-talk voice:   {PrettyKey(C.PushToTalkKey)}"),
            ("Make it yours",
             "Open the Customize tab for layout presets, colours, accessibility options, voice and more.\n\nYou can replay this tour from Customize → Updates & help at any time."),
        };
    }

    public void StartTour()
    {
        ShowByUser();
        ShowTabsNow();
        ShowTourStep(0);
    }

    public void ReplayTour() => StartTour();

    private void ShowTourStep(int i)
    {
        var steps = BuildTourSteps();
        if (i >= steps.Count) { FinishTour(); return; }
        _tourStep = i;
        TourStep.Text = $"STEP {i + 1} OF {steps.Count}";
        TourTitle.Text = steps[i].Title;
        TourBody.Text = steps[i].Body;
        TourNext.Content = i == steps.Count - 1 ? "Done" : "Next";
        TourLayer.Visibility = Visibility.Visible;
    }

    private void FinishTour()
    {
        TourLayer.Visibility = Visibility.Collapsed;
        _tourStep = -1;
        if (!C.TourDone) { C.TourDone = true; SettingsStore.Save(); }
    }

    private void TourNext_Click(object s, RoutedEventArgs e) => ShowTourStep(_tourStep + 1);
    private void TourSkip_Click(object s, MouseButtonEventArgs e) => FinishTour();

    // ---- share card ---------------------------------------------------------
    private int CurrentPositionMs()
    {
        if (_current == null) return 0;
        var pos = _current.ProgressMs + (_current.IsPlaying ? (int)(DateTime.UtcNow - _polledAtUtc).TotalMilliseconds : 0);
        return Math.Clamp(pos, 0, Math.Max(0, _current.DurationMs));
    }

    /// <summary>Makes a 1080×1350 picture of the current song, copies it to the clipboard and saves it in Pictures\Museek.</summary>
    public void CreateShareCard()
    {
        if (_current == null) { ShowHeard("Nothing is playing"); return; }
        try
        {
            var art = (ArtBorder.Background as ImageBrush)?.ImageSource;
            var lyric = _lyricIdx >= 0 && _lyricIdx < _lyricLines.Count ? _lyricLines[_lyricIdx].Text : null;
            var accent = ((SolidColorBrush)GetBrush("Accent")).Color;
            var frac = _current.DurationMs > 0 ? (double)CurrentPositionMs() / _current.DurationMs : 0;
            var img = ShareCard.Render(new ShareCardData(_current.Name, _current.Artist, _current.Album, art, frac, accent, lyric, C.EffectiveFont));

            var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Museek");
            System.IO.Directory.CreateDirectory(dir);
            var file = System.IO.Path.Combine(dir, $"Museek-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(img));
            using (var fs = System.IO.File.Create(file)) enc.Save(fs);
            try { Clipboard.SetImage(img); } catch { /* clipboard busy */ }
            ShowHeard("Share card copied · saved in Pictures\\Museek");
        }
        catch (Exception ex)
        {
            Log.Warn($"Share card failed: {ex.Message}");
            ShowHeard("Couldn't create the share card");
        }
    }

    // ---- small actions (also used by the command palette) ----------------------
    public void CopySongName()
    {
        if (_current == null) return;
        try { Clipboard.SetText($"{_current.Name} — {_current.Artist}"); ShowHeard("Copied song name"); } catch { /* clipboard busy */ }
    }

    public void OpenSongLink()
    {
        var url = _current == null ? null : _spotify.TrackUrl(_current.Id);
        if (url == null) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    public void SetAccent(string hex)
    {
        C.AccentColor = hex;
        SettingsStore.Save();
        ApplyTheme();
        LayoutViz();
    }

    // ---- updates --------------------------------------------------------------
    /// <summary>manual = the user asked (always report back); otherwise silent unless a new version exists.</summary>
    public async Task CheckForUpdatesAsync(bool manual)
    {
        if (manual) SetUpdateStatus("Checking for updates…");
        var (info, status) = await UpdateService.CheckAsync();
        _pendingUpdate = info;
        if (info != null)
        {
            CustomizeLink.Text = "Update available";
            SetUpdateStatus($"Version {info.Version} is available." + (info.Notes.Length > 0 ? "\n" + info.Notes : ""));
            if (_installBtn != null) _installBtn.Visibility = Visibility.Visible;
            UpdateAvailable?.Invoke(info);
            if (manual) ShowHeard($"Update {info.Version} available — see Customize → Updates & help");
        }
        else
        {
            CustomizeLink.Text = "Customize";
            if (manual) { SetUpdateStatus(status); ShowHeard(status); }
        }
    }

    private async Task InstallUpdateAsync()
    {
        if (_pendingUpdate == null) return;
        SetUpdateStatus("Downloading…");
        var progress = new Progress<int>(p => SetUpdateStatus($"Downloading… {p}%"));
        var (ok, message) = await UpdateService.DownloadAndInstallAsync(_pendingUpdate, progress);
        SetUpdateStatus(message);
        ShowHeard(message);
        if (ok) QuitRequested?.Invoke();
    }

    private void SetUpdateStatus(string text) { if (_updateStatus != null) _updateStatus.Text = text; }

    // ===================================================== customize tab ======

    private TextBlock Text(string text, double size, string fgKey, FontWeight? weight = null, Thickness? margin = null)
    {
        var tb = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        tb.SetResourceReference(TextBlock.ForegroundProperty, fgKey);
        if (weight != null) tb.FontWeight = weight.Value;
        if (margin != null) tb.Margin = margin.Value;
        return tb;
    }

    /// <summary>A collapsible card. Returns the body panel to add rows to.</summary>
    private StackPanel Section(string key, string title, bool defaultOpen)
    {
        var open = _sectionOpen.TryGetValue(key, out var o) ? o : defaultOpen;
        var body = new StackPanel { Margin = new Thickness(0, 10, 0, 0), Visibility = Vis(open) };
        var arrow = Text(open ? "▾" : "▸", 12, "FgFaint");
        arrow.HorizontalAlignment = HorizontalAlignment.Right;

        var head = new Grid { Background = Brushes.Transparent, Cursor = Cursors.Hand };
        head.Children.Add(Text(title, 12.5, "Fg", FontWeights.SemiBold));
        head.Children.Add(arrow);
        head.MouseLeftButtonUp += (_, _) =>
        {
            var now = body.Visibility != Visibility.Visible;
            body.Visibility = Vis(now);
            arrow.Text = now ? "▾" : "▸";
            _sectionOpen[key] = now;
        };

        var card = new Border
        {
            CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 11, 14, 12), Margin = new Thickness(0, 0, 0, 8),
        };
        card.SetResourceReference(Border.BackgroundProperty, "Card");
        card.SetResourceReference(Border.BorderBrushProperty, "CardBorder");

        var stack = new StackPanel();
        stack.Children.Add(head);
        stack.Children.Add(body);
        card.Child = stack;
        CustomizeHost.Children.Add(card);
        return body;
    }

    private void AddToggle(StackPanel body, string label, string? hint, bool value, Action<bool> onChange)
    {
        var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        text.Children.Add(Text(label, 12, "FgDim"));
        if (hint != null) text.Children.Add(Text(hint, 10.5, "FgFaint"));

        var cb = new CheckBox { IsChecked = value, Style = (Style)FindResource("Switch") };
        Grid.SetColumn(cb, 1);
        cb.Checked += (_, _) => onChange(true);
        cb.Unchecked += (_, _) => onChange(false);

        row.Children.Add(text);
        row.Children.Add(cb);
        body.Children.Add(row);
    }

    private void AddSlider(StackPanel body, string label, double min, double max, double value,
                           Func<double, string> fmt, Action<double> live, Action? commit = null)
    {
        var head = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        head.Children.Add(Text(label, 12, "FgDim"));
        var val = Text(fmt(value), 12, "Accent", FontWeights.Bold);
        val.HorizontalAlignment = HorizontalAlignment.Right;
        head.Children.Add(val);

        var sl = new Slider { Minimum = min, Maximum = max, Value = value, Style = (Style)FindResource("GlassSlider"), Margin = new Thickness(0, 4, 0, 4) };
        sl.ValueChanged += (_, e) => { val.Text = fmt(e.NewValue); live(e.NewValue); };
        sl.PreviewMouseLeftButtonUp += (_, _) => commit?.Invoke();
        body.Children.Add(head);
        body.Children.Add(sl);
    }

    private void AddChips(StackPanel body, string label, (string Key, string Text)[] items, string current, Action<string> onPick)
    {
        if (label.Length > 0) body.Children.Add(Text(label, 12, "FgDim", margin: new Thickness(0, 6, 0, 6)));
        var wrap = new WrapPanel { Margin = new Thickness(0, label.Length > 0 ? 0 : 6, 0, 0) };
        var group = "chips_" + Guid.NewGuid().ToString("N");
        foreach (var (key, text) in items)
        {
            var rb = new RadioButton { Content = text, Tag = key, GroupName = group, Style = (Style)FindResource("Chip"), IsChecked = key == current };
            rb.Checked += (_, _) => onPick(key);
            wrap.Children.Add(rb);
        }
        body.Children.Add(wrap);
    }

    private Button AddButton(StackPanel body, string text, Action onClick)
    {
        var b = new Button { Content = text, Style = (Style)FindResource("PillBtn"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        b.Click += (_, _) => onClick();
        body.Children.Add(b);
        return b;
    }

    private void AddSwatches(StackPanel body, string label, string[] colors, Func<string, bool> selected,
                             Action<string> pick, List<(string Hex, Border Swatch)> store)
    {
        body.Children.Add(Text(label, 12, "FgDim", margin: new Thickness(0, 6, 0, 6)));
        var wrap = new WrapPanel();
        foreach (var hex in colors)
        {
            var h = hex;
            var b = new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 8, 6),
                Background = new SolidColorBrush(ParseColor(h, Colors.Gray)), BorderThickness = new Thickness(2),
                BorderBrush = selected(h) ? Brushes.White : new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255)),
                Cursor = Cursors.Hand, ToolTip = h,
            };
            b.MouseLeftButtonUp += (_, _) => pick(h);
            wrap.Children.Add(b);
            store.Add((h, b));
        }
        body.Children.Add(wrap);
    }

    private void RefreshSwatchRings()
    {
        var s = C;
        foreach (var (hex, b) in _accentSwatches)
            b.BorderBrush = string.Equals(s.AccentColor, hex, StringComparison.OrdinalIgnoreCase)
                ? Brushes.White : new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255));
        foreach (var (hex, b) in _bgSwatches)
            b.BorderBrush = string.Equals(s.BgColorOverride ?? BgColors[0], hex, StringComparison.OrdinalIgnoreCase)
                ? Brushes.White : new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255));
    }

    private string DetectPreset()
    {
        var s = C;
        if (s.CompactMode && !s.ShowControls) return "minimal";
        if (s.CompactMode && s.ShowArt && s.ShowProgress && s.ShowControls) return "mini";
        if (!s.CompactMode && !s.ShowTabs) return "widgets";
        if (!s.CompactMode && s.ShowTabs && s.ShowArt && s.ShowProgress && s.ShowControls && s.ShowFooter) return "full";
        return "";
    }

    public void ApplyPreset(string key)
    {
        var s = C;
        switch (key)
        {
            case "full":
                s.CompactMode = false; s.ShowTabs = true;
                s.ShowArt = s.ShowProgress = s.ShowControls = s.ShowFooter = true;
                break;
            case "mini":
                s.CompactMode = true;
                s.ShowArt = s.ShowProgress = s.ShowControls = true;
                break;
            case "minimal":
                s.CompactMode = true; s.ShowArt = true; s.ShowProgress = false; s.ShowControls = false;
                break;
            case "widgets":
                s.CompactMode = false; s.ShowTabs = false;
                s.ShowArt = s.ShowProgress = s.ShowControls = true;
                break;
        }
        _compact = s.CompactMode;
        SettingsStore.Save();
        ApplyLayout();
        if (key == "widgets") { PopOut("queue"); if (s.LyricsEnabled) PopOut("lyrics"); }
        Dispatcher.BeginInvoke(new Action(BuildCustomize));          // refresh the toggles to match
    }

    /// <summary>Forget the built Customize UI (rebuilt next time it is shown, or right away if it is on screen).</summary>
    private void InvalidateCustomize()
    {
        _customizeBuilt = false;
        if (_tab == "settings") BuildCustomize();
    }

    private void BuildCustomize()
    {
        _customizeBuilt = true;
        CustomizeHost.Children.Clear();
        _accentSwatches.Clear();
        _bgSwatches.Clear();

        void Layout(Action set) { set(); SettingsStore.Save(); ApplyLayout(); }

        // ---------------------------------------------------------- look
        var lk = Section("look", "Look", true);
        lk.Children.Add(Text("Pick a style — each one sets colours, shapes and spacing. Tweak anything in the sections below afterwards.",
                             10.5, "FgFaint", margin: new Thickness(0, 0, 0, 8)));
        lk.Children.Add(LookGallery.Build(C.Look, ApplyLook, 140, GetBrush("Fg"), GetBrush("FgFaint"), GetBrush("Accent")));
        AddChips(lk, "Spacing", new[] { ("comfortable", "Comfortable"), ("compact", "Compact") }, C.Density,
                 k => { C.Density = k; SettingsStore.Save(); ApplyTheme(); });
        if (_lookUndo != null) AddButton(lk, "Undo last look change", UndoLook);

        // ---------------------------------------------------------- presets
        var p = Section("presets", "Layout presets", true);
        AddChips(p, "", new[] { ("full", "Full"), ("mini", "Mini"), ("minimal", "Minimal"), ("widgets", "Widgets mode") },
                 DetectPreset(), ApplyPreset);
        p.Children.Add(Text("Presets flip many options at once. Fine-tune everything in the sections below.", 10.5, "FgFaint", margin: new Thickness(0, 2, 0, 0)));

        // ---------------------------------------------------------- music source
        var src = Section("source", "Music source", true);
        AddChips(src, "", new[] { ("spotify", "Spotify"), ("windows", "Apple Music / Windows media") },
                 C.MusicSource == "windows" ? "windows" : "spotify",
                 k =>
                 {
                     if (C.MusicSource == k) return;
                     C.MusicSource = k; SettingsStore.Save();
                     Dispatcher.BeginInvoke(new Action(() => SourceChanged?.Invoke(k)));
                 });
        if (C.MusicSource == "windows")
        {
            src.Children.Add(Text("Follows the Apple Music app for Windows (or its website in Edge/Chrome), YouTube Music, iTunes, VLC…  " +
                                  "No queue, search or likes with this source — Windows doesn't share those.", 10.5, "FgFaint", margin: new Thickness(0, 2, 0, 6)));
            src.Children.Add(Text("Only follow this app (optional)", 12, "FgDim", margin: new Thickness(0, 4, 0, 4)));
            var frow = new Grid();
            frow.ColumnDefinitions.Add(new ColumnDefinition());
            frow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var ftb = new TextBox
            {
                Style = (Style)FindResource("GlassBox"), Text = C.MediaAppFilter == "auto" ? "" : C.MediaAppFilter,
                ToolTip = "e.g. apple, chrome, vlc. Leave empty for automatic (Apple Music first).",
            };
            ftb.PreviewMouseDown += Input_PreviewMouseDown;
            ftb.LostKeyboardFocus += Input_LostFocus;
            var fbtn = new Button { Content = "Set", Style = (Style)FindResource("PillBtn"), Margin = new Thickness(8, 0, 0, 0) };
            Grid.SetColumn(fbtn, 1);
            void SetFilter()
            {
                var t2 = ftb.Text.Trim();
                C.MediaAppFilter = t2.Length == 0 ? "auto" : t2;
                SettingsStore.Save();
                _ = PollAsync(true);
            }
            ftb.KeyDown += (_, e) => { if (e.Key == Key.Enter) SetFilter(); };
            fbtn.Click += (_, _) => SetFilter();
            frow.Children.Add(ftb);
            frow.Children.Add(fbtn);
            src.Children.Add(frow);
        }
        else
        {
            src.Children.Add(Text("Full features: queue, search & play, likes, volume.", 10.5, "FgFaint", margin: new Thickness(0, 2, 0, 0)));
        }

        // ---------------------------------------------------------- account
        var acct = Section("account", "Account & sign-in", false);
        acct.Children.Add(Text(_spotify.StatusText, 12, "FgDim"));
        AddToggle(acct, "Show the sign-in screen when Museek starts", "Continues by itself after 3 seconds once you're signed in",
                  C.ShowSignInOnStart, v => { C.ShowSignInOnStart = v; SettingsStore.Save(); });
        AddButton(acct, "Open the sign-in screen", () => WelcomeRequested?.Invoke());
        if (C.MusicSource != "windows") AddButton(acct, "Sign out of Spotify", () => SignOutRequested?.Invoke());

        // ---------------------------------------------------------- now playing card
        var n = Section("card", "Now playing card", false);
        AddToggle(n, "Album art", null, C.ShowArt, v => Layout(() => C.ShowArt = v));
        AddToggle(n, "\"Now playing\" label", null, C.ShowNowPlayingLabel, v => Layout(() => C.ShowNowPlayingLabel = v));
        AddToggle(n, "Artist name", null, C.ShowArtistName, v => Layout(() => C.ShowArtistName = v));
        AddToggle(n, "Album name", null, C.ShowAlbum, v => Layout(() => C.ShowAlbum = v));
        AddToggle(n, "Progress bar", "Click it to seek", C.ShowProgress, v => Layout(() => C.ShowProgress = v));
        AddToggle(n, "Time labels", null, C.ShowTimeText, v => Layout(() => C.ShowTimeText = v));
        AddToggle(n, "Playback controls", null, C.ShowControls, v => Layout(() => C.ShowControls = v));
        AddToggle(n, "Shuffle button", null, C.ShowShuffleButton, v => Layout(() => C.ShowShuffleButton = v));
        AddToggle(n, "Repeat button", null, C.ShowRepeatButton, v => Layout(() => C.ShowRepeatButton = v));
        AddToggle(n, "Like button", null, C.ShowLikeButton, v => Layout(() => C.ShowLikeButton = v));
        AddToggle(n, "Lock button in title bar", null, C.ShowLockButton, v => Layout(() => C.ShowLockButton = v));
        AddToggle(n, "Red close dot", null, C.ShowCloseButton, v => Layout(() => C.ShowCloseButton = v));
        AddToggle(n, "Status bar at the bottom", null, C.ShowFooter, v => Layout(() => C.ShowFooter = v));

        // ---------------------------------------------------------- panels
        var t = Section("panels", "Panels & tabs", false);
        AddToggle(t, "Show the tab panel", "Up Next / Lyrics / Chat / Visuals / Customize", C.ShowTabs, v => Layout(() => C.ShowTabs = v));
        AddToggle(t, "Mini view", "Hides tabs and status bar (double-click the title bar)", _compact, v => SetCompact(v));
        AddToggle(t, "Lyrics", null, C.LyricsEnabled, v => Layout(() => C.LyricsEnabled = v));
        AddToggle(t, "Chat (Gemini)", null, C.ChatbotEnabled, v => Layout(() => C.ChatbotEnabled = v));
        t.Children.Add(Text("Tip: with the tab panel off, right-click the overlay → Customize to get back here.", 10.5, "FgFaint", margin: new Thickness(0, 4, 0, 0)));

        // ---------------------------------------------------------- widgets
        var w = Section("widgets", "Pop-out widgets", false);
        w.Children.Add(Text("Split a panel into its own floating window. Move it, resize it from any edge, put it on another monitor.", 10.5, "FgFaint", margin: new Thickness(0, 0, 0, 4)));
        foreach (var key in WidgetKeys)
        {
            var k = key;
            AddToggle(w, TitleFor(k), null, _widgets.ContainsKey(k), v => { if (v) PopOut(k); else Dock(k); });
        }
        AddButton(w, "Dock all", () => { DockAllWidgets(); Dispatcher.BeginInvoke(new Action(BuildCustomize)); });

        // ---------------------------------------------------------- appearance
        var a = Section("appearance", "Appearance", false);
        AddSwatches(a, "Accent colour", AccentColors, h => string.Equals(C.AccentColor, h, StringComparison.OrdinalIgnoreCase),
                    h => { C.AccentColor = h; SettingsStore.Save(); ApplyTheme(); LayoutViz(); }, _accentSwatches);

        var hexRow = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        hexRow.ColumnDefinitions.Add(new ColumnDefinition());
        hexRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var hex = new TextBox { Style = (Style)FindResource("GlassBox"), ToolTip = "Custom accent colour, e.g. #FF5500" };
        hex.PreviewMouseDown += Input_PreviewMouseDown;
        hex.LostKeyboardFocus += Input_LostFocus;
        var apply = new Button { Content = "Set accent", Style = (Style)FindResource("PillBtn"), Margin = new Thickness(8, 0, 0, 0) };
        Grid.SetColumn(apply, 1);
        void ApplyHex()
        {
            var txt = hex.Text.Trim();
            if (txt.Length == 0) return;
            if (!txt.StartsWith("#")) txt = "#" + txt;
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(txt);
                C.AccentColor = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                SettingsStore.Save();
                hex.Clear();
                ApplyTheme(); LayoutViz();
            }
            catch { hex.ToolTip = "Not a valid colour — use something like #FF5500"; }
        }
        hex.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyHex(); };
        apply.Click += (_, _) => ApplyHex();
        hexRow.Children.Add(hex);
        hexRow.Children.Add(apply);
        a.Children.Add(hexRow);

        AddSwatches(a, "Background colour", BgColors, h => string.Equals(C.BgColorOverride ?? BgColors[0], h, StringComparison.OrdinalIgnoreCase),
                    h => { C.BgColorOverride = h == BgColors[0] ? null : h; SettingsStore.Save(); ApplyTheme(); }, _bgSwatches);

        AddChips(a, "Theme", new[] { ("dark", "Dark"), ("light", "Light") }, C.ThemeMode,
                 k => { C.ThemeMode = k; SettingsStore.Save(); ApplyTheme(); });
        AddChips(a, "Background style", new[] { ("solid", "Solid"), ("transparent", "Glass (see-through)") }, C.BackgroundMode,
                 k => { C.BackgroundMode = k; SettingsStore.Save(); UpdatePanelBrush(); });
        AddSlider(a, "Background opacity", 20, 100, Math.Round(C.OverlayOpacity * 100), v => $"{(int)v}%",
                  v => { C.OverlayOpacity = v / 100.0; UpdatePanelBrush(); }, () => SettingsStore.Save());
        AddSlider(a, "Corner radius", 0, 32, C.CornerRadius, v => $"{(int)v}",
                  v => { C.CornerRadius = (int)v; ApplyCorners(); }, () => SettingsStore.Save());
        AddSlider(a, "Border thickness", 0, 4, C.BorderWidth, v => $"{(int)v}",
                  v => { C.BorderWidth = (int)v; ApplyCorners(); }, () => SettingsStore.Save());
        AddSlider(a, "Progress bar thickness", 3, 14, C.ProgressBarHeight, v => $"{(int)v}",
                  v => { C.ProgressBarHeight = (int)v; ProgressHost.Height = (int)v; ProgressHandle.Margin = new Thickness(-7, -(14 - (int)v) / 2.0, 0, 0); },
                  () => SettingsStore.Save());
        AddToggle(a, "Ambient album-art glow", null, C.AmbientGlow, v =>
        {
            C.AmbientGlow = v; SettingsStore.Save(); GlowLayer.Visibility = Vis(v); _lastArtUrl = null;
        });

        // ---------------------------------------------------------- accessibility
        var acc = Section("access", "Accessibility", false);
        AddToggle(acc, "High contrast", "Black & white with a yellow accent and thick borders (also follows Windows high-contrast mode)",
                  C.HighContrast, v => { C.HighContrast = v; SettingsStore.Save(); ApplyTheme(); LayoutViz(); });
        AddToggle(acc, "Reduce motion", "No fades and no animated visualizer (also follows Windows' animation setting)",
                  C.ReduceMotion, v => { C.ReduceMotion = v; SettingsStore.Save(); ApplyTheme(); StartStopViz(); });
        AddToggle(acc, "Easy-read font", "Verdana: wider and clearer letters", C.EasyReadFont,
                  v => { C.EasyReadFont = v; SettingsStore.Save(); ApplyTheme(); });
        var zoomPending = C.UiScale;
        AddSlider(acc, "Text & interface size", 70, 160, Math.Round(C.UiScale * 100), x => $"{(int)x}%",
                  x => zoomPending = x / 100.0, () => ApplyZoom(zoomPending));
        acc.Children.Add(Text("Icon buttons have screen-reader labels. The overlay never takes keyboard focus, so use the hotkeys " +
                              "or the command palette to control it from the keyboard.", 10.5, "FgFaint", margin: new Thickness(0, 6, 0, 0)));

        // ---------------------------------------------------------- behaviour
        var b = Section("behaviour", "Behaviour", false);
        AddToggle(b, "Always on top", null, C.AlwaysOnTop, SetAlwaysOnTop);
        AddToggle(b, "Efficiency mode", "Lower priority + efficiency cores. Turn off only if it ever feels laggy",
                  C.EfficiencyMode, v => { C.EfficiencyMode = v; SettingsStore.Save(); ProcessEfficiency.Apply(v); });
        AddToggle(b, "Click-through lock", "Turn off with Ctrl+Alt+L or the tray menu", _locked, v => { if (v != _locked) ToggleLock(); });
        AddToggle(b, "Scroll wheel changes volume", "Over the now-playing card", C.WheelVolume, v => { C.WheelVolume = v; SettingsStore.Save(); });
        AddToggle(b, "Hide when nothing is playing", null, C.AutoHideWhenIdle, v => { C.AutoHideWhenIdle = v; SettingsStore.Save(); });
        AddToggle(b, "Notify on song change", null, C.NotifyOnTrackChange, v => { C.NotifyOnTrackChange = v; SettingsStore.Save(); });
        if (AutostartService.IsAvailable)
            AddToggle(b, "Start with Windows", null, AutostartService.IsEnabled(), v => AutostartService.SetEnabled(v));
        AddSlider(b, "Spotify refresh (Windows media refreshes every ~1.5 s)", 2, 15, Math.Round(C.PollIntervalMs / 1000.0), v => $"every {(int)v} s",
                  v => { C.PollIntervalMs = (int)v * 1000; }, () => { SettingsStore.Save(); _pollTimer.Interval = NextPollInterval(); });
        b.Children.Add(Text("Higher = less CPU and network. The progress bar still moves smoothly in between.", 10.5, "FgFaint"));

        // ---------------------------------------------------------- voice
        var vsec = Section("voice", "Voice", false);
        AddToggle(vsec, "Voice commands", null, C.VoiceEnabled, v => { C.VoiceEnabled = v; SettingsStore.Save(); InputsChanged?.Invoke(); });
        AddToggle(vsec, "Push-to-talk", "Recommended — far fewer false triggers", C.PushToTalkEnabled,
                  v => { C.PushToTalkEnabled = v; SettingsStore.Save(); InputsChanged?.Invoke(); });
        vsec.Children.Add(Text($"Push-to-talk key: {PrettyKey(C.PushToTalkKey)}", 10.5, "FgFaint", margin: new Thickness(0, 2, 0, 6)));

        vsec.Children.Add(Text("Wake word (always-listening mode)", 12, "FgDim", margin: new Thickness(0, 6, 0, 4)));
        var wrow = new Grid();
        wrow.ColumnDefinitions.Add(new ColumnDefinition());
        wrow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var wtb = new TextBox { Style = (Style)FindResource("GlassBox"), Text = C.WakeWord, ToolTip = "1-3 plain words, e.g. hey music" };
        wtb.PreviewMouseDown += Input_PreviewMouseDown;
        wtb.LostKeyboardFocus += Input_LostFocus;
        var wbtn = new Button { Content = "Set", Style = (Style)FindResource("PillBtn"), Margin = new Thickness(8, 0, 0, 0) };
        Grid.SetColumn(wbtn, 1);
        void SetWake()
        {
            var cleaned = System.Text.RegularExpressions.Regex.Replace(wtb.Text.ToLowerInvariant(), @"[^a-z' ]", "").Trim();
            var words = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length is < 1 or > 3) { ShowHeard("Wake word must be 1-3 plain words"); return; }
            C.WakeWord = string.Join(' ', words);
            wtb.Text = C.WakeWord;
            SettingsStore.Save();
            InputsChanged?.Invoke();
            ShowHeard($"Wake word set to “{C.WakeWord}”");
        }
        wtb.KeyDown += (_, e) => { if (e.Key == Key.Enter) SetWake(); };
        wbtn.Click += (_, _) => SetWake();
        wrow.Children.Add(wtb);
        wrow.Children.Add(wbtn);
        vsec.Children.Add(wrow);
        vsec.Children.Add(Text("Only used when push-to-talk is off. Pick something distinctive — common words trigger by accident.",
                               10.5, "FgFaint", margin: new Thickness(0, 4, 0, 4)));

        AddChips(vsec, "Chatbot speaks its answers", new[] { ("off", "Never"), ("voice", "When I ask by voice"), ("always", "Always") },
                 C.SpeakMode, k => { C.SpeakMode = k; SettingsStore.Save(); });
        AddButton(vsec, "Stop speaking", SpeechService.Stop);

        // ---------------------------------------------------------- hotkeys
        var h2 = Section("hotkeys", "Hotkeys", false);
        foreach (var (key, label) in new[]
        {
            ("play_pause", "Play / pause"), ("next_track", "Next song"), ("previous_track", "Previous song"),
            ("toggle_overlay", "Show / hide overlay"), ("toggle_chatbot", "Chat"), ("toggle_lyrics", "Lyrics"),
            ("command_palette", "Command palette"), ("quick_search", "Search & play"), ("like_track", "Like song"), ("toggle_lock", "Lock"),
        })
        {
            if (!C.Hotkeys.TryGetValue(key, out var combo)) continue;
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(Text(label, 11, "FgFaint"));
            var kbd = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Right, CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1, 6, 1), BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = string.Join(" + ", combo.Split('+').Select(x => x.Length > 0 ? char.ToUpper(x[0]) + x[1..] : x)),
                    FontFamily = new FontFamily("Consolas"), FontSize = 10.5,
                },
            };
            kbd.SetResourceReference(Border.BackgroundProperty, "Card");
            kbd.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
            ((TextBlock)kbd.Child).SetResourceReference(TextBlock.ForegroundProperty, "FgDim");
            row.Children.Add(kbd);
            h2.Children.Add(row);
        }
        AddButton(h2, "Edit hotkeys & more…", () => OpenSettingsRequested?.Invoke());

        // ---------------------------------------------------------- updates & help
        var upd = Section("updates", "Updates & help", false);
        upd.Children.Add(Text($"Museek v{AppInfo.Version}", 12, "FgDim"));
        AddToggle(upd, "Check for updates once a day", UpdateService.EffectiveUrl.Length == 0 ? "This build has no update source configured" : null,
                  C.UpdateCheckEnabled, v => { C.UpdateCheckEnabled = v; SettingsStore.Save(); });
        AddButton(upd, "Check for updates now", () => _ = CheckForUpdatesAsync(true));
        _updateStatus = Text(_pendingUpdate != null ? $"Version {_pendingUpdate.Version} is available." : "", 10.5, "FgFaint", margin: new Thickness(0, 6, 0, 0));
        upd.Children.Add(_updateStatus);
        _installBtn = AddButton(upd, "Install update & restart", () => _ = InstallUpdateAsync());
        _installBtn.Visibility = _pendingUpdate != null ? Visibility.Visible : Visibility.Collapsed;
        AddButton(upd, "Replay the tour", ReplayTour);
        AddButton(upd, "Create share card of this song", CreateShareCard);

        // ---------------------------------------------------------- reset
        var r = Section("reset", "Reset", false);
        AddButton(r, "Reset overlay to defaults", () =>
        {
            if (MessageBox.Show("Reset the overlay's layout, colours and options to defaults?\n(Your Spotify / Gemini credentials are kept.)",
                    "Museek", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            SettingsStore.ResetToDefaults();
            ApplyAfterReset();
            InputsChanged?.Invoke();
        });
    }

    private void CustomizeLink_Click(object s, MouseButtonEventArgs e) => OpenCustomize();

    // ===================================================== context menu / QoL ===
    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        MenuItem Item(string header, Action act, bool check = false)
        {
            var it = new MenuItem { Header = header, IsCheckable = check };
            it.Click += (_, _) => act();
            return it;
        }

        var mini = Item("Mini view", () => SetCompact(!_compact), true);
        var custom = Item("Customize…", OpenCustomize);

        var popMenu = new MenuItem { Header = "Pop out panel" };
        var popItems = new Dictionary<string, MenuItem>();
        foreach (var key in WidgetKeys)
        {
            var k = key;
            var it = new MenuItem { Header = TitleFor(k), IsCheckable = true };
            it.Click += (_, _) => { if (_widgets.ContainsKey(k)) Dock(k); else PopOut(k); Dispatcher.BeginInvoke(new Action(InvalidateCustomize)); };
            popItems[k] = it;
            popMenu.Items.Add(it);
        }

        var copy = Item("Copy song name", CopySongName);
        var open = Item("Open song in Spotify", OpenSongLink);
        var share = Item("Create share card", CreateShareCard);
        var pal = Item("Command palette…", () => PaletteRequested?.Invoke());
        var top = Item("Always on top", () => SetAlwaysOnTop(!C.AlwaysOnTop), true);
        var lockItem = Item("Lock position (click-through)", ToggleLock, true);
        var hide = Item("Hide overlay", HideByUser);

        menu.Items.Add(pal); menu.Items.Add(mini); menu.Items.Add(custom); menu.Items.Add(popMenu);
        menu.Items.Add(new Separator());
        menu.Items.Add(copy); menu.Items.Add(open); menu.Items.Add(share);
        menu.Items.Add(new Separator());
        menu.Items.Add(top); menu.Items.Add(lockItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(hide);

        menu.Opened += (_, _) =>
        {
            mini.IsChecked = _compact;
            top.IsChecked = C.AlwaysOnTop;
            lockItem.IsChecked = _locked;
            foreach (var kv in popItems) kv.Value.IsChecked = _widgets.ContainsKey(kv.Key);
            copy.IsEnabled = _current != null;
            open.Visibility = _spotify.TrackUrl(_current?.Id ?? "x") != null ? Visibility.Visible : Visibility.Collapsed;
            open.IsEnabled = _current != null;
        };
        return menu;
    }

    private void NowPlaying_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!C.WheelVolume || _current == null) return;
        if (!_spotify.Caps.HasFlag(MusicCaps.Volume))
        {
            if (!_spotify.Caps.HasFlag(MusicCaps.VolumeKeys)) return;
            if (e.Delta > 0) _ = _spotify.VolumeUpAsync(4); else _ = _spotify.VolumeDownAsync(4);   // system volume keys
            ShowHeard(e.Delta > 0 ? "Volume up" : "Volume down");
            e.Handled = true;
            return;
        }
        if (_wheelVol < 0) _wheelVol = (int)VolumeSlider.Value;
        _wheelVol = Math.Clamp(_wheelVol + (e.Delta > 0 ? 5 : -5), 0, 100);
        ShowHeard($"Volume {_wheelVol}%");
        _wheelTimer.Stop();
        _wheelTimer.Start();                         // send one request after the wheel stops
        e.Handled = true;
    }

    private async Task CommitWheelVolume()
    {
        _wheelTimer.Stop();
        var v = _wheelVol;
        _wheelVol = -1;
        if (v < 0) return;
        _uiUpdating = true; VolumeSlider.Value = v; VolVal.Text = $"{v}%"; _uiUpdating = false;
        await _spotify.SetVolumeAsync(v);
    }

    // ============================================================= events ===
    private async void Play_Click(object s, RoutedEventArgs e) { await _spotify.PlayPauseAsync(); RefreshSoon(); }
    private async void Next_Click(object s, RoutedEventArgs e) { await _spotify.NextTrackAsync(); RefreshSoon(); }
    private async void Prev_Click(object s, RoutedEventArgs e) { await _spotify.PreviousTrackAsync(); RefreshSoon(); }

    private async void Shuffle_Click(object s, RoutedEventArgs e)
    {
        if (_current == null) return;
        await _spotify.ToggleShuffleAsync(_current.ShuffleState);
        RefreshSoon();
    }

    private async void Repeat_Click(object s, RoutedEventArgs e)
    {
        if (_current == null) return;
        await _spotify.CycleRepeatAsync(_current.RepeatState);
        RefreshSoon();
    }

    private async void Like_Click(object s, RoutedEventArgs e)
    {
        var r = await _spotify.ToggleLikeCurrentAsync();
        if (r != null) SetLiked(r.Value);
    }

    private void DotClose_Click(object s, MouseButtonEventArgs e) => HideByUser();
    private void DotCompact_Click(object s, MouseButtonEventArgs e) => SetCompact(!_compact);
    private void DotExpand_Click(object s, MouseButtonEventArgs e) { C.ShowTabs = true; SetCompact(false); }
    private void Lock_Click(object s, RoutedEventArgs e) => ToggleLock();
    private void Pin_Click(object s, RoutedEventArgs e) => SetAlwaysOnTop(!Topmost);

    // ----- progress bar: click to seek --------------------------------------
    private async void Progress_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_current == null || ProgressHost.ActualWidth <= 0) return;
        var frac = Math.Clamp(e.GetPosition(ProgressHost).X / ProgressHost.ActualWidth, 0, 1);
        var ms = (int)(frac * _current.DurationMs);
        _current = _current with { ProgressMs = ms };
        _polledAtUtc = DateTime.UtcNow;
        _liveDirty = true;
        UpdateLiveUi();
        await _spotify.SeekToAsync(ms);
    }

    private void Progress_MouseEnter(object s, MouseEventArgs e) => FadeHandle(1, 120);
    private void Progress_MouseLeave(object s, MouseEventArgs e) => FadeHandle(0, 160);

    private void FadeHandle(double to, int ms)
    {
        if (ReduceMotion) { ProgressHandle.BeginAnimation(OpacityProperty, null); ProgressHandle.Opacity = to; return; }
        ProgressHandle.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)));
    }

    // ----- queue --------------------------------------------------------------
    private async void AddTrack_Click(object s, RoutedEventArgs e) => await AddToQueueAsync();

    private async void AddTrackInput_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await AddToQueueAsync();
    }

    private async Task AddToQueueAsync()
    {
        var q = AddTrackInput.Text.Trim();
        if (q.Length == 0) return;
        QueueStatus.Text = "Searching…";
        var (ok, title) = await _spotify.AddToQueueAsync(q);
        QueueStatus.Text = ok ? $"Added: {title}" : "Couldn't find or add that song.";
        if (ok) { AddTrackInput.Clear(); RefreshSoon(); }
    }

    // ----- volume -------------------------------------------------------------
    private void VolumeSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        VolVal.Text = $"{(int)e.NewValue}%";
    }

    private void VolumeSlider_MouseDown(object s, MouseButtonEventArgs e) => _volDragging = true;

    private async void VolumeSlider_MouseUp(object s, MouseButtonEventArgs e)
    {
        await Task.Delay(30);                                      // let IsMoveToPoint settle the value
        await _spotify.SetVolumeAsync((int)VolumeSlider.Value);
        _volDragging = false;
    }

    // ----- chat ---------------------------------------------------------------
    private async void ChatInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var text = ChatInput.Text.Trim();
        if (text.Length == 0) return;
        ChatInput.Clear();
        AppendChat("You: " + text);
        await _onChatMessage(text);
    }

    // The overlay/widgets are normally no-activate; allow keyboard focus only while typing.
    private void Input_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBox tb) FocusInput(tb);
    }

    private void Input_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is DependencyObject d && Window.GetWindow(d) is Window win)
            WinNative.SetExStyle(WinNative.Handle(win), WinNative.WS_EX_NOACTIVATE, true);
    }

    private void FocusInput(TextBox tb)
    {
        var win = Window.GetWindow(tb) ?? this;
        WinNative.SetExStyle(WinNative.Handle(win), WinNative.WS_EX_NOACTIVATE, false);
        win.Activate();
        tb.Focus();
        Keyboard.Focus(tb);
    }

    // ============================================================ helpers ===
    private bool IsInteractive(DependencyObject d)
    {
        for (DependencyObject? o = d; o != null;
             o = (o is Visual || o is System.Windows.Media.Media3D.Visual3D)
                 ? VisualTreeHelper.GetParent(o) : LogicalTreeHelper.GetParent(o))
        {
            if (o == ProgressHost) return true;
            if (o is System.Windows.Controls.Primitives.ButtonBase or TextBox or System.Windows.Controls.Primitives.Thumb
                or System.Windows.Controls.Primitives.ScrollBar or Slider or Ellipse) return true;
        }
        return false;
    }

    /// <summary>If the saved position is on a monitor that no longer exists, bring it back.</summary>
    private void EnsureOnScreen()
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var probe = new System.Drawing.Rectangle(
                (int)((Left + WinMargin) * dpi.DpiScaleX), (int)((Top + WinMargin) * dpi.DpiScaleY),
                (int)(120 * dpi.DpiScaleX), (int)(40 * dpi.DpiScaleY));
            foreach (var scr in System.Windows.Forms.Screen.AllScreens)
                if (scr.Bounds.IntersectsWith(probe)) return;
            Left = 40; Top = 40;
        }
        catch { Left = 40; Top = 40; }
    }

    private void ForceTopmost() => WinNative.ForceTopmost(_hwnd, Topmost && IsVisible);

    /// <summary>15-second safety net in case a foreground event was missed.</summary>
    private void TopTick()
    {
        if (_hwnd == IntPtr.Zero || !IsVisible || !Topmost || _drag.IsDragging) return;
        ForceTopmost();
        foreach (var w in _widgets.Values) w.ForceTopmost();
    }

    private static string MsToMmss(int ms)
    {
        var total = Math.Max(0, ms) / 1000;
        return $"{total / 60}:{total % 60:00}";
    }

    protected override void OnClosed(EventArgs e)
    {
        _closing = true;
        _pollTimer.Stop(); _uiTimer.Stop(); _vizTimer.Stop(); _topTimer.Stop(); _wheelTimer.Stop(); _changeDebounce.Stop();
        UnhookForeground();
        foreach (var w in _widgets.Values.ToList()) { w.Detach(); w.Close(); }
        base.OnClosed(e);
    }
}
