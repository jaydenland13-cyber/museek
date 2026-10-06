using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;

namespace Museek.Services;

/// <summary>Saved geometry / state of a pop-out widget window.</summary>
public sealed class WidgetState
{
    public bool Open { get; set; }
    public bool Placed { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; } = 380;
    public double H { get; set; } = 440;
}

/// <summary>All user-adjustable values. Defaults match the original museek_config.py.</summary>
public sealed class AppSettings
{
    // ---- credentials -------------------------------------------------------
    /// <summary>"spotify" or "windows" (Apple Music & anything else using Windows media controls). Empty = ask on first run.</summary>
    public string MusicSource { get; set; } = "";
    /// <summary>Windows media mode: "auto" (Apple Music first) or part of an app name to follow, e.g. "chrome".</summary>
    public string MediaAppFilter { get; set; } = "auto";
    public string SpotifyClientId { get; set; } = "";
    /// <summary>Gemini key, DPAPI-encrypted for the current Windows user.</summary>
    public string GeminiApiKeyProtected { get; set; } = "";
    public string ChatbotModel { get; set; } = "gemini-3.6-flash";

    // ---- behaviour ---------------------------------------------------------
    public int PollIntervalMs { get; set; } = 5000;
    public bool ShowUpNext { get; set; } = true;
    public bool CompactMode { get; set; } = false;
    public bool AutoHideWhenIdle { get; set; } = false;
    public bool NotifyOnTrackChange { get; set; } = false;
    public int VolumeStep { get; set; } = 10;
    public bool ChatbotEnabled { get; set; } = true;
    public bool LyricsEnabled { get; set; } = true;
    public bool QuickSearchEnabled { get; set; } = true;
    public bool UpdateCheckEnabled { get; set; } = true;
    public string UpdateCheckUrl { get; set; } = "";

    // ---- voice -------------------------------------------------------------
    public bool VoiceEnabled { get; set; } = true;
    public string WakeWord { get; set; } = "hey music";
    public bool PushToTalkEnabled { get; set; } = true;
    public string PushToTalkKey { get; set; } = "ctrl+alt+v";

    // ---- appearance --------------------------------------------------------
    public int OverlayWidth { get; set; } = 432;      // window size in DIPs (includes the 16px shadow margin)
    public int OverlayHeight { get; set; } = 690;
    public int OverlayX { get; set; } = 40;
    public int OverlayY { get; set; } = 40;
    public double OverlayOpacity { get; set; } = 0.88;
    public string AccentColor { get; set; } = "#8B5CF6";
    public string ThemeMode { get; set; } = "dark";          // dark | light
    public string BackgroundMode { get; set; } = "solid";    // solid | transparent
    public string FontFamily { get; set; } = "Inter, Segoe UI Variable Text, Segoe UI";
    public double FontScale { get; set; } = 1.0;
    public int CornerRadius { get; set; } = 24;
    public int BorderWidth { get; set; } = 1;
    public string? BgColorOverride { get; set; }
    public string? TextColorOverride { get; set; }
    public string LyricsColor { get; set; } = "";
    public bool ShowNowPlayingLabel { get; set; } = true;
    public bool ShowArtistName { get; set; } = true;
    public bool ShowTimeText { get; set; } = true;
    public bool ShowCloseButton { get; set; } = true;
    public bool ShowLockButton { get; set; } = true;
    public bool ShowSettingsButton { get; set; } = true;
    public bool ShowShuffleButton { get; set; } = true;
    public bool ShowRepeatButton { get; set; } = true;
    public bool ShowLikeButton { get; set; } = true;
    public int ProgressBarHeight { get; set; } = 8;
    public bool AmbientGlow { get; set; } = true;
    // look (see Looks.cs)
    public string Look { get; set; } = "aurora";
    public string PlayStyle { get; set; } = "gradient";   // gradient | solid | square | outline | ghost
    public string DotStyle { get; set; } = "color";       // color | mono
    public string Density { get; set; } = "comfortable";  // comfortable | compact
    public int CardRadius { get; set; } = 12;
    public int ControlRadius { get; set; } = 12;
    public bool ProgressRound { get; set; } = true;
    public bool BorderAccent { get; set; }
    // sign-in / welcome screen
    public bool WelcomeDone { get; set; }
    public bool ShowSignInOnStart { get; set; } = true;
    // layout / customisation
    public bool ShowArt { get; set; } = true;
    public bool ShowAlbum { get; set; } = true;
    public bool ShowProgress { get; set; } = true;
    public bool ShowControls { get; set; } = true;
    public bool ShowFooter { get; set; } = true;
    public bool ShowTabs { get; set; } = true;
    public bool AlwaysOnTop { get; set; } = true;
    public bool WheelVolume { get; set; } = true;
    public bool EfficiencyMode { get; set; } = true;
    // accessibility
    public bool HighContrast { get; set; }
    public bool ReduceMotion { get; set; }
    public bool EasyReadFont { get; set; }
    // help / voice / updates
    public bool TourDone { get; set; }
    public string SpeakMode { get; set; } = "voice";      // off | voice (only when asked by voice) | always
    public long LastUpdateCheckTicks { get; set; }

    [JsonIgnore]
    public string EffectiveFont => EasyReadFont ? "Verdana, Segoe UI" : (string.IsNullOrWhiteSpace(FontFamily) ? "Segoe UI" : FontFamily);
    public Dictionary<string, WidgetState> Widgets { get; set; } = new();
    public double UiScale { get; set; } = 1.0;          // overall overlay size, 0.55 - 1.6
    public int SettingsVersion { get; set; } = 0;
    public string EqPreset { get; set; } = "flat";   // flat | bass | vocal | electronic | treble

    // ---- hotkeys -----------------------------------------------------------
    public Dictionary<string, string> Hotkeys { get; set; } = DefaultHotkeys();

    public static Dictionary<string, string> DefaultHotkeys() => new()
    {
        ["play_pause"]     = "ctrl+alt+space",
        ["next_track"]     = "ctrl+alt+right",
        ["previous_track"] = "ctrl+alt+left",
        ["toggle_overlay"] = "ctrl+alt+m",
        ["toggle_chatbot"] = "ctrl+alt+c",
        ["toggle_lock"]    = "ctrl+alt+l",
        ["volume_up"]      = "ctrl+alt+up",
        ["volume_down"]    = "ctrl+alt+down",
        ["quick_search"]   = "ctrl+alt+f",
        ["like_track"]     = "ctrl+alt+u",
        ["command_palette"] = "ctrl+alt+p",
        ["toggle_lyrics"]  = "ctrl+alt+y",
    };

    // ---- Gemini key helpers ------------------------------------------------
    public string GetGeminiKey()
    {
        // Environment variable / .env still win, for parity with the Python build.
        var env = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

        var dotenv = Path.Combine(Log.BaseDir, ".env");
        if (File.Exists(dotenv))
        {
            foreach (var line in File.ReadAllLines(dotenv))
            {
                var t = line.Trim();
                if (t.StartsWith("GEMINI_API_KEY=", StringComparison.Ordinal))
                {
                    var v = t["GEMINI_API_KEY=".Length..].Trim().Trim('"', '\'');
                    if (v.Length > 0) return v;
                }
            }
        }

        if (string.IsNullOrEmpty(GeminiApiKeyProtected)) return "";
        try
        {
            var raw = ProtectedData.Unprotect(Convert.FromBase64String(GeminiApiKeyProtected),
                null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(raw);
        }
        catch { return ""; }
    }

    public void SetGeminiKey(string key)
    {
        GeminiApiKeyProtected = string.IsNullOrWhiteSpace(key)
            ? ""
            : Convert.ToBase64String(ProtectedData.Protect(
                Encoding.UTF8.GetBytes(key.Trim()), null, DataProtectionScope.CurrentUser));
    }
}

/// <summary>settings.json next to the exe.</summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static string PathOnDisk => Path.Combine(Log.BaseDir, "settings.json");

    public static AppSettings Current { get; private set; } = new();

    public static void Load()
    {
        try
        {
            if (File.Exists(PathOnDisk))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(PathOnDisk, Encoding.UTF8));
                if (loaded != null) Current = loaded;
            }
        }
        catch (Exception ex) { Log.Warn($"Could not read settings.json, using defaults: {ex.Message}"); }

        Current.Hotkeys ??= AppSettings.DefaultHotkeys();
        foreach (var kv in AppSettings.DefaultHotkeys())
            Current.Hotkeys.TryAdd(kv.Key, kv.Value);

        // One-time migration for settings saved by older builds.
        if (Current.SettingsVersion < 2)
        {
            Current.PushToTalkEnabled = true;                       // always-listening picked up the music itself
            if (Current.WakeWord == "music") Current.WakeWord = "hey music";
            if (Current.PushToTalkKey == "f9") Current.PushToTalkKey = "ctrl+alt+v";
            if (Current.PollIntervalMs < 5000) Current.PollIntervalMs = 5000;
            Current.SettingsVersion = 2;
            Save();
        }
        if (Current.SettingsVersion < 3)
        {
            Current.OverlayWidth = 432;                              // window can now be resized freely in both directions
            Current.OverlayHeight = 690;
            Current.SettingsVersion = 3;
            Save();
        }
        if (Current.SettingsVersion < 4)
        {
            if (string.IsNullOrEmpty(Current.MusicSource))
                Current.MusicSource = string.IsNullOrWhiteSpace(Current.SpotifyClientId) ? "" : "spotify";   // existing Spotify users stay on Spotify
            Current.SettingsVersion = 4;
            Save();
        }
        if (Current.SettingsVersion < 5)
        {
            if (!string.IsNullOrEmpty(Current.MusicSource)) Current.WelcomeDone = true;   // existing users skip the first-run flow
            Current.SettingsVersion = 5;
            Save();
        }
        Current.Widgets ??= new();
        Current.OverlayWidth = Math.Clamp(Current.OverlayWidth, 360, 4000);
        Current.OverlayHeight = Math.Clamp(Current.OverlayHeight, 470, 4000);
        Current.UiScale = Math.Clamp(Current.UiScale <= 0 ? 1.0 : Current.UiScale, 0.7, 1.6);
    }

    private static DispatcherTimer? _saveTimer;
    private static bool _dirty;

    /// <summary>Queues a save (coalesced: many quick changes = one disk write). Call <see cref="Flush"/> to write now.</summary>
    public static void Save()
    {
        _dirty = true;
        var disp = System.Windows.Application.Current?.Dispatcher;
        if (disp == null) { Flush(); return; }
        if (_saveTimer == null)
        {
            _saveTimer = new DispatcherTimer(DispatcherPriority.Background, disp) { Interval = TimeSpan.FromMilliseconds(600) };
            _saveTimer.Tick += (_, _) => Flush();
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Writes settings.json immediately if anything changed (also called on exit).</summary>
    public static void Flush()
    {
        _saveTimer?.Stop();
        if (!_dirty) return;
        _dirty = false;
        try { File.WriteAllText(PathOnDisk, JsonSerializer.Serialize(Current, Json), Encoding.UTF8); }
        catch (Exception ex) { Log.Warn($"Could not save settings.json: {ex.Message}"); }
    }

    /// <summary>Reset everything except credentials.</summary>
    public static void ResetToDefaults()
    {
        Current = new AppSettings
        {
            MusicSource = Current.MusicSource,
            MediaAppFilter = Current.MediaAppFilter,
            SpotifyClientId = Current.SpotifyClientId,
            GeminiApiKeyProtected = Current.GeminiApiKeyProtected,
            SettingsVersion = 5,
            WelcomeDone = Current.WelcomeDone,
            ShowSignInOnStart = Current.ShowSignInOnStart,
        };
        _dirty = true;
        Flush();
    }

    public static void Export(string dest) { _dirty = true; Flush(); File.Copy(PathOnDisk, dest, true); }

    public static void Import(string src)
    {
        var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(src, Encoding.UTF8));
        if (loaded == null) return;
        if (string.IsNullOrEmpty(loaded.SpotifyClientId)) loaded.SpotifyClientId = Current.SpotifyClientId;
        Current = loaded;
        Save();
    }
}
