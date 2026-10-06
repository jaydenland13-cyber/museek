using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Animation;
using Museek.Services;
using Museek.Views;

namespace Museek;

/// <summary>
/// Entry point. Wiring (same as the original main.py):
///   SpotifyService      -> Spotify Web API
///   OverlayWindow       -> the always-on-top UI
///   HotkeyManager       -> global hotkeys
///   VoiceCommandListener-> "music ..." voice commands
///   ChatbotService      -> overlay chat panel + "ask ..." voice command
/// Everything is wrapped so a crash writes crash_log.txt and shows a message box
/// (the app has no console, so otherwise failures would just vanish).
/// </summary>
public partial class App : Application
{

    private Mutex? _singleInstance;
    private MusicHub _music = null!;
    private ChatbotService _chatbot = null!;
    private OverlayWindow _overlay = null!;
    private HotkeyManager _hotkeys = null!;
    private VoiceCommandListener? _voice;
    private TrayService? _tray;
    private readonly System.Collections.Generic.List<int> _hotkeyIds = new();
    private SettingsWindow? _settingsWindow;

    public App()
    {
        // Cap every animation to 20 FPS: this overlay doesn't need 60, and it cuts GPU/CPU use a lot.
        try { Timeline.DesiredFrameRateProperty.OverrideMetadata(typeof(Timeline), new FrameworkPropertyMetadata(20)); }
        catch { /* already set */ }
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, ex) => { ShowCrash(ex.Exception); ex.Handled = true; Shutdown(1); };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => ShowCrash(ex.ExceptionObject as Exception);

        var wi = Array.IndexOf(e.Args, "--wait-pid");           // set by the updater when it restarts us
        if (wi >= 0 && wi + 1 < e.Args.Length && int.TryParse(e.Args[wi + 1], out var oldPid))
        {
            try { using var old = Process.GetProcessById(oldPid); await Task.Run(() => old.WaitForExit(15000)); }
            catch { /* already gone */ }
        }

        _singleInstance = new Mutex(true, "Museek.SingleInstance", out var isNew);
        if (!isNew) { Shutdown(); return; }

        try { await StartAsync(); }
        catch (Exception ex) { ShowCrash(ex); Shutdown(1); }
    }

    private async Task StartAsync()
    {
        Log.Init();
        SettingsStore.Load();
        var s = SettingsStore.Current;
        ProcessEfficiency.Apply(s.EfficiencyMode);

        _music = new MusicHub(new SpotifyService(), new WindowsMediaService());
        await StartupAuthAsync();

        _chatbot = new ChatbotService(_music);
        _overlay = new OverlayWindow(_music, OnChatMessage);
        _overlay.TrackChanged += t =>
        {
            if (SettingsStore.Current.NotifyOnTrackChange) _tray?.Notify($"{t.Name} — {t.Artist}", "Now Playing");
        };
        _overlay.OpenSettingsRequested += OpenSettings;
        _overlay.MicClicked += OnMicClicked;
        _overlay.InputsChanged += ReapplyInputs;
        _overlay.QuitRequested += Quit;
        _overlay.WelcomeRequested += () => _ = ShowWelcomeAsync(WelcomeMode.SignIn);
        _overlay.SignOutRequested += async () =>
        {
            _music.SignOutSpotify();
            _overlay.OnSourceChanged();
            await ShowWelcomeAsync(WelcomeMode.SignIn);
        };
        _overlay.PaletteRequested += OpenPalette;
        _overlay.UpdateAvailable += info =>
            _tray?.Notify($"Version {info.Version} is available — open Customize → Updates & help.", "Museek update");
        _overlay.SourceChanged += async _ =>
        {
            await _music.ConnectIfNeededAsync();
            if (SettingsStore.Current.MusicSource == "spotify" && !_music.IsConnected)
            {
                await ShowWelcomeAsync(WelcomeMode.SignIn);                  // not signed in yet: show the sign-in screen
                return;
            }
            _overlay.OnSourceChanged();
        };
        _overlay.EqPresetChosen += OnEqPreset;
        _overlay.Show();

        // Restore the saved EQ preset and show whether Equalizer APO is available.
        if (s.EqPreset != "flat") _overlay.SetEqStatus(EqualizerService.Apply(s.EqPreset).message);
        else _overlay.SetEqStatus(EqualizerService.IsInstalled
            ? "Equalizer APO detected — pick a preset."
            : "Needs the free Equalizer APO app to change the sound. Pick a preset for setup help.");

        _hotkeys = new HotkeyManager();
        RegisterHotkeys();
        StartVoice();

        _tray = new TrayService(
            show: () => _overlay.ShowByUser(),
            hide: () => _overlay.HideByUser(),
            toggleLock: () => _overlay.ToggleLock(),
            openSettings: OpenSettings,
            quit: Quit);

        UpdateService.CleanupOldVersion();
        if (s.UpdateCheckEnabled && UpdateService.EffectiveUrl.Length > 0 &&
            DateTime.UtcNow - new DateTime(Math.Max(0, s.LastUpdateCheckTicks), DateTimeKind.Utc) > TimeSpan.FromHours(24))
            _ = DailyUpdateCheckAsync();

        Log.Info("Museek is running.");
        MemoryTrim.RunSoon(15);
    }

    // ------------------------------------------------------------ hotkeys ---
    private void RegisterHotkeys()
    {
        var s = SettingsStore.Current;
        var actions = new System.Collections.Generic.Dictionary<string, Action>
        {
            ["play_pause"]     = () => { _ = _music.PlayPauseAsync(); _overlay.RefreshSoon(); },
            ["next_track"]     = () => { _ = _music.NextTrackAsync(); _overlay.RefreshSoon(); },
            ["previous_track"] = () => { _ = _music.PreviousTrackAsync(); _overlay.RefreshSoon(); },
            ["toggle_overlay"] = () => _overlay.ToggleVisibility(),
            ["toggle_chatbot"] = () => _overlay.ToggleChatPanel(),
            ["toggle_lock"]    = () => _overlay.ToggleLock(),
            ["volume_up"]      = () => _ = _music.VolumeUpAsync(SettingsStore.Current.VolumeStep),
            ["volume_down"]    = () => _ = _music.VolumeDownAsync(SettingsStore.Current.VolumeStep),
        };
        actions["command_palette"] = OpenPalette;
        if (s.QuickSearchEnabled) actions["quick_search"] = OpenQuickSearch;
        if (s.ShowLikeButton) actions["like_track"] = () => _ = LikeAsync();
        if (s.LyricsEnabled) actions["toggle_lyrics"] = () => _overlay.ToggleLyricsPanel();

        foreach (var (name, combo) in s.Hotkeys)
            if (actions.TryGetValue(name, out var cb))
                _hotkeyIds.Add(_hotkeys.Register(combo, cb));
    }

    private async Task LikeAsync()
    {
        if (!_music.Caps.HasFlag(MusicCaps.Like))
        {
            _overlay.ShowHeard($"Liking songs needs Spotify ({_music.Name} can't)");
            return;
        }
        var r = await _music.ToggleLikeCurrentAsync();
        if (r != null) _overlay.SetLiked(r.Value);
    }

    private void OpenQuickSearch()
    {
        if (!_music.Caps.HasFlag(MusicCaps.Search))
        {
            _overlay.ShowHeard($"Search & play needs Spotify ({_music.Name} can't search)");
            return;
        }
        new QuickSearchWindow(async q => (await _music.SearchAndPlayAsync(q)).ok).Show();
        _overlay.RefreshSoon();
    }

    // -------------------------------------------------------------- voice ---
    private void StartVoice()
    {
        var s = SettingsStore.Current;
        if (!s.VoiceEnabled)
        {
            Log.Info("Voice commands disabled in settings.");
            _overlay.SetMicState(false, s.PushToTalkEnabled);
            return;
        }
        _voice = new VoiceCommandListener(RouteVoiceCommand, _hotkeys);
        _voice.Heard += _overlay.ShowHeard;
        _voice.Start();
        _overlay.SetMicState(_voice.IsRunning, s.PushToTalkEnabled);
        if (_voice.IsRunning)
            _overlay.ShowHeard(s.PushToTalkEnabled
                ? $"Voice ready — press {s.PushToTalkKey} and speak"
                : $"Listening — say “{s.WakeWord} …”");
    }

    /// <summary>Mic button: push-to-talk = listen once; otherwise toggle voice on/off.</summary>
    private void OnMicClicked()
    {
        var s = SettingsStore.Current;
        if (s.VoiceEnabled && s.PushToTalkEnabled && _voice != null) { _voice.PushToTalk(); return; }

        s.VoiceEnabled = !s.VoiceEnabled;
        SettingsStore.Save();
        StopVoice();
        StartVoice();
        if (!s.VoiceEnabled) _overlay.ShowHeard("Voice off");
    }

    private void OnEqPreset(string preset)
    {
        var (_, message) = EqualizerService.Apply(preset);
        _overlay.SetEqStatus(message);
    }

    private void StopVoice() { _voice?.Dispose(); _voice = null; _overlay?.SetMicState(false, SettingsStore.Current.PushToTalkEnabled); }

    /// <summary>Maps a spoken command to an action (same phrases as the original).</summary>
    private async void RouteVoiceCommand(string command)
    {
        var c = command.ToLowerInvariant().Trim();
        var s = SettingsStore.Current;

        if (c is "play" or "pause" or "play pause" or "toggle") { await _music.PlayPauseAsync(); _overlay.RefreshSoon(); }
        else if (c is "next" or "skip" or "next track" or "skip track") { await _music.NextTrackAsync(); _overlay.RefreshSoon(); }
        else if (c is "previous" or "back" or "previous track" or "go back") { await _music.PreviousTrackAsync(); _overlay.RefreshSoon(); }
        else if (c is "volume up" or "louder" or "turn it up") await _music.VolumeUpAsync(s.VolumeStep);
        else if (c is "volume down" or "quieter" or "turn it down") await _music.VolumeDownAsync(s.VolumeStep);
        else if (MoodIntent.TryParse(c, out var moodRequest))                    // "play something chill"
        {
            _overlay.AppendChat("You (voice): " + c);
            await HandleMoodAsync(moodRequest, true);
        }
        else if (s.QuickSearchEnabled && (c.StartsWith("play ") || c.StartsWith("search ")))
        {
            var query = c[(c.IndexOf(' ') + 1)..];
            if (!_music.Caps.HasFlag(MusicCaps.Search))
            {
                _overlay.AppendChat($"Museek: Search & play needs Spotify — {_music.Name} can't search.");
                return;
            }
            var (ok, title) = await _music.SearchAndPlayAsync(query);
            _overlay.AppendChat(ok ? "Museek: Playing " + title : "Museek: Could not find/play: " + query);
            _overlay.RefreshSoon();
        }
        else if (c is "like" or "save this" or "like this song") await LikeAsync();
        else if (c is "show" or "show overlay") _overlay.ShowByUser();
        else if (c is "hide" or "hide overlay") _overlay.HideByUser();
        else if (c is "what's playing" or "whats playing" or "what is playing")
        {
            var t = await _music.GetCurrentTrackAsync();
            _overlay.AppendChat(t != null ? $"Museek: {t.Name} by {t.Artist}" : "Museek: Nothing is playing right now.");
        }
        else if (c is "stop talking" or "be quiet") SpeechService.Stop();
        else if (c.StartsWith("ask "))
        {
            var q = c["ask ".Length..];
            _overlay.AppendChat("You (voice): " + q);
            await AskAsync(q, true);
        }
    }

    // ---------------------------------------------------------------- chat ---
    /// <summary>Typed message from the overlay's chat box.</summary>
    private Task OnChatMessage(string message) => AskAsync(message, false);

    private async Task AskAsync(string message, bool viaVoice)
    {
        if (MoodIntent.TryParse(message, out var mood)) { await HandleMoodAsync(mood, viaVoice); return; }
        var answer = await _chatbot.AskAsync(message);
        _overlay.AppendChat("Museek: " + answer);
        MaybeSpeak(answer, viaVoice);
    }

    private static void MaybeSpeak(string text, bool viaVoice)
    {
        var mode = SettingsStore.Current.SpeakMode;
        if (mode == "always" || (mode == "voice" && viaVoice)) SpeechService.Speak(text);
    }

    /// <summary>"Play something chill": Gemini picks songs, the first plays and the rest are queued (Spotify). Other sources just get the ideas.</summary>
    private async Task HandleMoodAsync(string request, bool viaVoice)
    {
        if (!_chatbot.IsEnabled)
        {
            _overlay.AppendChat("Museek: Mood requests use the chatbot — add a Gemini API key in Settings first.");
            return;
        }
        _overlay.AppendChat($"Museek: Finding songs for {(request.Length == 0 ? "something you'd enjoy" : "“" + request + "”")}…");

        var songs = await _chatbot.SuggestSongsAsync(request, 5);
        if (songs.Count == 0)
        {
            _overlay.AppendChat("Museek: I couldn't come up with songs for that — try describing it differently.");
            return;
        }

        var list = string.Join("\n", songs.Select(x => $"• {x.Title} — {x.Artist}"));
        if (!_music.Caps.HasFlag(MusicCaps.Search))
        {
            _overlay.AppendChat($"Museek: Here are some ideas (playing by name needs Spotify):\n{list}");
            MaybeSpeak("Here are some ideas. " + string.Join(", ", songs.Select(x => x.Title)), viaVoice);
            return;
        }

        var (ok, title) = await _music.SearchAndPlayAsync($"{songs[0].Title} {songs[0].Artist}");
        var queued = 0;
        if (ok)
        {
            await Task.Delay(800);                                   // let playback start so the queue accepts songs
            foreach (var song in songs.Skip(1))
                if ((await _music.AddToQueueAsync($"{song.Title} {song.Artist}")).ok) queued++;
        }

        _overlay.AppendChat(ok
            ? $"Museek: Playing {title}" + (queued > 0 ? $" and queued {queued} more:\n{list}" : "")
            : "Museek: I found ideas but couldn't start playback — is Spotify active on a device?\n" + list);
        if (ok) { MaybeSpeak($"Playing {title}", viaVoice); _overlay.RefreshSoon(); }
    }

    // ------------------------------------------------------------ command palette ---
    private CommandPaletteWindow? _palette;

    private static readonly (string Name, string Hex)[] PaletteAccents =
    {
        ("Purple", "#8B5CF6"), ("Cyan", "#06B6D4"), ("Pink", "#EC4899"), ("Green", "#1DB954"),
        ("Orange", "#F97316"), ("Blue", "#3B82F6"), ("Red", "#EF4444"), ("Yellow", "#EAB308"),
    };

    private void OpenPalette()
    {
        if (_palette != null) { _palette.Close(); return; }
        _palette = new CommandPaletteWindow(_overlay.Resources, BuildPaletteItems);
        _palette.Closed += (_, _) => _palette = null;
        _palette.Show();
    }

    private async Task SearchPlayAsync(string query)
    {
        var (ok, title) = await _music.SearchAndPlayAsync(query);
        _overlay.ShowHeard(ok ? $"Playing {title}" : $"Couldn't find “{query}”");
        _overlay.RefreshSoon();
    }

    private async Task SwitchSourceAsync(string source)
    {
        SettingsStore.Current.MusicSource = source;
        SettingsStore.Save();
        await _music.ConnectIfNeededAsync();
        if (source == "spotify" && !_music.IsConnected) { await ShowWelcomeAsync(WelcomeMode.SignIn); return; }
        _overlay.OnSourceChanged();
    }

    private List<PaletteItem> BuildPaletteItems(string query)
    {
        var s = SettingsStore.Current;
        var caps = _music.Caps;
        var all = new List<PaletteItem>();
        void Add(string title, string hint, Action run, string kw = "") => all.Add(new PaletteItem(title, hint, run, kw));

        // playback
        Add("Play / pause", "Playback", () => { _ = _music.PlayPauseAsync(); _overlay.RefreshSoon(); }, "toggle resume stop");
        Add("Next song", "Playback", () => { _ = _music.NextTrackAsync(); _overlay.RefreshSoon(); }, "skip forward");
        Add("Previous song", "Playback", () => { _ = _music.PreviousTrackAsync(); _overlay.RefreshSoon(); }, "back");
        if (caps.HasFlag(MusicCaps.Like)) Add("Like / unlike this song", "Playback", () => _ = LikeAsync(), "heart save favourite favorite");
        Add("Volume up", "Playback", () => _ = _music.VolumeUpAsync(s.VolumeStep), "louder");
        Add("Volume down", "Playback", () => _ = _music.VolumeDownAsync(s.VolumeStep), "quieter");

        // voice
        Add(s.PushToTalkEnabled ? "Listen for a voice command" : "Turn voice on / off", "Voice", OnMicClicked, "mic microphone talk speak");
        Add("Stop speaking", "Voice", SpeechService.Stop, "quiet silence tts");

        // overlay & layout
        Add("Show / hide overlay", "Overlay", () => _overlay.ToggleVisibility());
        Add("Mini view", "Overlay", () => _overlay.SetCompact(true), "compact small");
        Add("Full view", "Overlay", () => { SettingsStore.Current.ShowTabs = true; _overlay.SetCompact(false); }, "expand big");
        Add("Customize…", "Overlay", () => _overlay.OpenCustomize(), "settings options");
        Add("Lock position (click-through)", "Overlay", () => _overlay.ToggleLock(), "ghost");
        Add("Always on top: on / off", "Overlay", () => _overlay.SetAlwaysOnTop(!SettingsStore.Current.AlwaysOnTop), "pin float");
        Add("Layout: Full", "Layout", () => _overlay.ApplyPreset("full"));
        Add("Layout: Mini", "Layout", () => _overlay.ApplyPreset("mini"));
        Add("Layout: Minimal", "Layout", () => _overlay.ApplyPreset("minimal"));
        Add("Layout: Widgets mode", "Layout", () => _overlay.ApplyPreset("widgets"), "split");
        Add("Pop out: Up Next", "Widgets", () => _overlay.PopOut("queue"), "queue window");
        Add("Pop out: Lyrics", "Widgets", () => _overlay.PopOut("lyrics"), "window");
        Add("Pop out: Chat", "Widgets", () => _overlay.PopOut("chat"), "window");
        Add("Pop out: Visuals & sound", "Widgets", () => _overlay.PopOut("visuals"), "window");
        Add("Dock all widgets", "Widgets", () => _overlay.DockAllWidgets(), "attach");

        // appearance & accessibility
        foreach (var (name, hex) in PaletteAccents)
        {
            var h = hex;
            Add($"Accent: {name}", "Appearance", () => _overlay.SetAccent(h), "colour color theme");
        }
        foreach (var look in Looks.All)
        {
            var l = look;
            Add($"Look: {l.Name}", "Appearance", () => _overlay.ApplyLook(l), "style theme design " + l.Blurb);
        }
        Add("Theme: dark", "Appearance", () => { s.ThemeMode = "dark"; SettingsStore.Save(); _overlay.ApplyTheme(); });
        Add("Theme: light", "Appearance", () => { s.ThemeMode = "light"; SettingsStore.Save(); _overlay.ApplyTheme(); });
        Add("High contrast: on / off", "Accessibility", () => { s.HighContrast = !s.HighContrast; SettingsStore.Save(); _overlay.ApplyTheme(); });
        Add("Reduce motion: on / off", "Accessibility", () => { s.ReduceMotion = !s.ReduceMotion; SettingsStore.Save(); _overlay.ApplyTheme(); }, "animation");
        Add("Easy-read font: on / off", "Accessibility", () => { s.EasyReadFont = !s.EasyReadFont; SettingsStore.Save(); _overlay.ApplyTheme(); }, "verdana dyslexia");

        // source & tools
        Add("Open the sign-in screen", "Account", () => _ = ShowWelcomeAsync(WelcomeMode.SignIn), "login log in welcome connect");
        Add("Music source: Spotify", "Source", () => _ = SwitchSourceAsync("spotify"));
        Add("Music source: Apple Music / Windows media", "Source", () => _ = SwitchSourceAsync("windows"), "apple itunes youtube");
        Add("Create share card", "Tools", _overlay.CreateShareCard, "image picture instagram");
        Add("Copy song name", "Tools", _overlay.CopySongName);
        if (_music.TrackUrl("x") != null) Add("Open song in Spotify", "Tools", _overlay.OpenSongLink);
        Add("Check for updates", "Tools", () => _ = _overlay.CheckForUpdatesAsync(true), "upgrade version");
        Add("Replay the tour", "Tools", _overlay.ReplayTour, "help tutorial");
        Add("Quit Museek", "App", Quit, "exit close");

        // filter
        var q = query.Trim();
        var tokens = q.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var result = tokens.Length == 0
            ? all.Take(9).ToList()
            : all.Where(i => { var hay = (i.Title + " " + i.Hint + " " + i.Keywords).ToLowerInvariant(); return tokens.All(t => hay.Contains(t)); })
                 .OrderByDescending(i => i.Title.ToLowerInvariant().StartsWith(tokens[0]) ? 1 : 0).ToList();

        // actions built from the text itself
        if (q.Length > 0)
        {
            var m = Regex.Match(q, @"^volume\s+(\d{1,3})$", RegexOptions.IgnoreCase);
            if (m.Success && caps.HasFlag(MusicCaps.Volume))
            {
                var v = Math.Clamp(int.Parse(m.Groups[1].Value), 0, 100);
                result.Insert(0, new PaletteItem($"Set volume to {v}%", "Playback", () => _ = _music.SetVolumeAsync(v)));
            }
            m = Regex.Match(q, @"^seek\s+(\d{1,3}):(\d{2})$", RegexOptions.IgnoreCase);
            if (m.Success && caps.HasFlag(MusicCaps.Seek))
            {
                var ms = (int.Parse(m.Groups[1].Value) * 60 + int.Parse(m.Groups[2].Value)) * 1000;
                result.Insert(0, new PaletteItem($"Jump to {m.Groups[1].Value}:{m.Groups[2].Value}", "Playback", () => _ = _music.SeekToAsync(ms)));
            }
            m = Regex.Match(q, @"^accent\s+#?([0-9a-f]{6})$", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var hex = "#" + m.Groups[1].Value.ToUpperInvariant();
                result.Insert(0, new PaletteItem($"Set accent colour {hex}", "Appearance", () => _overlay.SetAccent(hex)));
            }

            result.Add(new PaletteItem($"Play something: {q}", "Chatbot picks songs", () => { _overlay.ShowChat(); _ = HandleMoodAsync(q, false); }, "mood"));
            if (caps.HasFlag(MusicCaps.Search))
                result.Add(new PaletteItem($"Search & play: {q}", _music.Name, () => _ = SearchPlayAsync(q)));
            if (s.ChatbotEnabled)
                result.Add(new PaletteItem($"Ask the chatbot: {q}", "Chat", () => { _overlay.ShowChat(); _overlay.AppendChat("You: " + q); _ = AskAsync(q, false); }));
        }
        return result.Take(9).ToList();
    }

    // ------------------------------------------------------------ settings ---
    private void OpenSettings()
    {
        if (_settingsWindow != null) { _settingsWindow.Activate(); return; }
        _settingsWindow = new SettingsWindow(OnSettingsSaved, OnSettingsReset);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    private void ReapplyInputs()
    {
        StopVoice();
        _hotkeys.UnregisterAll();
        _hotkeyIds.Clear();
        RegisterHotkeys();
        StartVoice();
    }

    private async void OnSettingsSaved()
    {
        _overlay.ApplyTheme();
        ReapplyInputs();
        var s = SettingsStore.Current;
        Log.Info($"Settings applied: wake word='{s.WakeWord}'");
        await _music.ConnectIfNeededAsync();
        _overlay.OnSourceChanged();
    }

    private void OnSettingsReset()
    {
        _overlay.ApplyAfterReset();
        ReapplyInputs();
        Log.Info("Settings reset to defaults.");
    }

    // ----------------------------------------------------------------- misc ---
    // ------------------------------------------------------------ sign-in / welcome ---
    /// <summary>
    /// First run: the full welcome flow. Later starts: reuse the saved login silently; show the sign-in screen only when a
    /// login is needed, or when "show the sign-in screen when Museek starts" is on (it then continues by itself after 3 s).
    /// </summary>
    private async Task StartupAuthAsync()
    {
        var s = SettingsStore.Current;
        var firstRun = !s.WelcomeDone || string.IsNullOrEmpty(s.MusicSource);

        if (!firstRun) await _music.TryResumeAsync();                         // silent: never opens the browser
        var needsLogin = s.MusicSource == "spotify" && !_music.IsConnected;

        if (firstRun || needsLogin || s.ShowSignInOnStart)
        {
            new WelcomeWindow(_music, firstRun ? WelcomeMode.FirstRun : WelcomeMode.SignIn).ShowDialog();
            if (string.IsNullOrEmpty(s.MusicSource))                          // window closed early on first run: still be usable
                s.MusicSource = string.IsNullOrWhiteSpace(s.SpotifyClientId) ? "windows" : "spotify";
            if (!s.WelcomeDone) s.WelcomeDone = true;
            SettingsStore.Save();
        }
        await _music.ConnectIfNeededAsync();
    }

    /// <summary>Opens the welcome / sign-in screen on demand (Customize → Account, command palette).</summary>
    private async Task ShowWelcomeAsync(WelcomeMode mode)
    {
        new WelcomeWindow(_music, mode).ShowDialog();
        SettingsStore.Save();
        await _music.ConnectIfNeededAsync();
        _overlay.OnSourceChanged();
        _overlay.ApplyTheme();                                                // the look may have changed
    }

    /// <summary>At most once a day, 20 s after start-up (never while the app is still loading).</summary>
    private async Task DailyUpdateCheckAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(20));
        SettingsStore.Current.LastUpdateCheckTicks = DateTime.UtcNow.Ticks;
        SettingsStore.Save();
        await _overlay.CheckForUpdatesAsync(false);
    }

    private void Quit()
    {
        Dispatcher.Invoke(() =>
        {
            _hotkeys?.Dispose();
            StopVoice();
            _tray?.Dispose();
            _overlay?.Close();
            Log.Info("Museek shut down.");
            Shutdown();
        });
    }

    private static void ShowCrash(Exception? ex)
    {
        var details = ex?.ToString() ?? "Unknown error";
        string msg;
        try
        {
            var path = Path.Combine(Log.BaseDir, "crash_log.txt");
            File.WriteAllText(path, details);
            msg = "Museek hit an error and had to close.\n\nDetails saved to:\n" + path;
        }
        catch { msg = "Museek hit an error and had to close.\n\n" + details; }
        MessageBox.Show(msg, "Museek", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SettingsStore.Flush();                       // settings saves are coalesced: write anything pending
        Log.Info($"App exiting (code {e.ApplicationExitCode})");
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
