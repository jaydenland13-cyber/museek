using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Media;
using Windows.Media.Control;

namespace Museek.Services;

/// <summary>
/// Reads and controls whatever is playing through Windows' built-in media session
/// (the same thing the volume flyout / lock-screen "now playing" card uses).
/// Works with the Apple Music app for Windows, Apple Music / YouTube Music in a browser,
/// iTunes, VLC and most other players. No login needed.
///
/// Limits: no queue, no search, no "like", and volume only via the system volume keys.
/// </summary>
public sealed class WindowsMediaService : IMusicSource
{
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    private const byte VK_VOLUME_DOWN = 0xAE, VK_VOLUME_UP = 0xAF;
    private const uint KEYEVENTF_KEYUP = 0x2;

    private GlobalSystemMediaTransportControlsSessionManager? _mgr;
    private GlobalSystemMediaTransportControlsSession? _session;
    private GlobalSystemMediaTransportControlsSession? _hooked;
    private string? _hookedId;
    private DateTime _lastTimelineEvt = DateTime.MinValue;

    /// <summary>Windows tells us when the song / play state changes, so the overlay doesn't have to poll while a song plays.</summary>
    public event Action? Changed;
    private string? _appName;
    private string? _artId, _artPath;

    public string Name => "Windows media";
    public bool IsConnected => _mgr != null;
    public string? AuthError { get; private set; }
    public bool IsLocal => true;
    public MusicCaps Caps => MusicCaps.VolumeKeys | MusicCaps.ShuffleRepeat | MusicCaps.Seek;

    public string StatusText => _mgr == null
        ? "Windows media controls unavailable"
        : _appName == null ? "Waiting for a media app (open Apple Music and play something)"
                           : $"{_appName} · via Windows media";

    public string? TrackUrl(string trackId) => null;

    // -------------------------------------------------------------- connect ----
    public async Task ConnectAsync()
    {
        try
        {
            _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _mgr.SessionsChanged += (_, _) => { Hook(PickSession()); Changed?.Invoke(); };
            _mgr.CurrentSessionChanged += (_, _) => { Hook(PickSession()); Changed?.Invoke(); };
            Hook(PickSession());
            AuthError = null;
            Log.Info("Connected to Windows media controls.");
        }
        catch (Exception ex)
        {
            _mgr = null;
            AuthError = ex.Message;
            Log.Error($"Windows media controls unavailable: {ex.Message}");
        }
    }

    // ------------------------------------------------------ session selection ----
    private static bool IsApple(string id)
    {
        id = id.ToLowerInvariant();
        return id.Contains("apple") || id.Contains("itunes");
    }

    private static bool IsPlaying(GlobalSystemMediaTransportControlsSession s)
    {
        try { return s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
        catch { return false; }
    }

    /// <summary>Which app to follow: your filter (if set), else Apple Music first, else whatever is playing.</summary>
    private GlobalSystemMediaTransportControlsSession? PickSession()
    {
        if (_mgr == null) return null;
        var sessions = _mgr.GetSessions().ToList();
        var filter = (SettingsStore.Current.MediaAppFilter ?? "auto").Trim().ToLowerInvariant();
        var custom = filter.Length > 0 && filter != "auto";

        var candidates = custom
            ? sessions.Where(s => s.SourceAppUserModelId.ToLowerInvariant().Contains(filter)).ToList()
            : sessions;
        if (candidates.Count == 0) return null;

        return candidates.FirstOrDefault(s => IsApple(s.SourceAppUserModelId) && IsPlaying(s))
            ?? candidates.FirstOrDefault(IsPlaying)
            ?? candidates.FirstOrDefault(s => IsApple(s.SourceAppUserModelId))
            ?? candidates.FirstOrDefault(s => _mgr.GetCurrentSession()?.SourceAppUserModelId == s.SourceAppUserModelId)
            ?? candidates[0];
    }

    private static string FriendlyName(string aumid)
    {
        var id = aumid.ToLowerInvariant();
        if (id.Contains("applemusic") || (id.Contains("apple") && id.Contains("music"))) return "Apple Music";
        if (id.Contains("itunes")) return "iTunes";
        if (id.Contains("spotify")) return "Spotify";
        if (id.Contains("msedge")) return "Edge";
        if (id.Contains("chrome")) return "Chrome";
        if (id.Contains("firefox")) return "Firefox";
        if (id.Contains("vlc")) return "VLC";
        var cut = aumid.IndexOfAny(new[] { '!', '_' });
        var name = cut > 0 ? aumid[..cut] : aumid;
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    // ------------------------------------------------------------- change events ----
    private void Hook(GlobalSystemMediaTransportControlsSession? s)
    {
        try
        {
            var id = s?.SourceAppUserModelId;
            if (id == _hookedId && _hooked != null) return;                       // already following this app
            if (_hooked != null)
            {
                _hooked.MediaPropertiesChanged -= OnProps;
                _hooked.PlaybackInfoChanged -= OnPlayback;
                _hooked.TimelinePropertiesChanged -= OnTimeline;
            }
            _hooked = s; _hookedId = id;
            if (s == null) return;
            s.MediaPropertiesChanged += OnProps;
            s.PlaybackInfoChanged += OnPlayback;
            s.TimelinePropertiesChanged += OnTimeline;
        }
        catch (Exception ex) { Log.Info($"Could not subscribe to media events: {ex.Message}"); }
    }

    private void OnProps(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) => Changed?.Invoke();
    private void OnPlayback(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) => Changed?.Invoke();
    private void OnTimeline(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e)
    {
        if (DateTime.UtcNow - _lastTimelineEvt < TimeSpan.FromSeconds(2)) return;   // some apps fire this constantly
        _lastTimelineEvt = DateTime.UtcNow;
        Changed?.Invoke();
    }

    // ---------------------------------------------------------------- reads ----
    public async Task<TrackInfo?> GetCurrentTrackAsync()
    {
        if (_mgr == null) return null;
        try
        {
            var session = PickSession();
            _session = session;
            Hook(session);
            if (session == null) { _appName = null; return null; }
            _appName = FriendlyName(session.SourceAppUserModelId);

            var props = await session.TryGetMediaPropertiesAsync();
            if (props == null || string.IsNullOrWhiteSpace(props.Title)) return null;

            var info = session.GetPlaybackInfo();
            var tl = session.GetTimelineProperties();
            var playing = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            var durMs = (int)Math.Max(0, (tl.EndTime - tl.StartTime).TotalMilliseconds);
            var posMs = (tl.Position - tl.StartTime).TotalMilliseconds;
            if (playing && tl.LastUpdatedTime != default)                       // position is "as of" LastUpdatedTime
                posMs += Math.Max(0, (DateTimeOffset.UtcNow - tl.LastUpdatedTime).TotalMilliseconds);
            posMs = durMs > 0 ? Math.Clamp(posMs, 0, durMs) : Math.Max(0, posMs);

            var artist = props.Artist ?? "";
            var album = props.AlbumTitle ?? "";
            if (album.Length == 0 && artist.Contains(" — "))                    // some apps send "Artist — Album" in one field
            {
                var parts = artist.Split(" — ", 2);
                artist = parts[0]; album = parts[1];
            }

            var id = $"{props.Title}|{artist}|{album}";
            var art = await GetArtAsync(id, props);

            var repeat = info?.AutoRepeatMode switch
            {
                MediaPlaybackAutoRepeatMode.Track => "track",
                MediaPlaybackAutoRepeatMode.List => "context",
                _ => "off",
            };

            return new TrackInfo(id, props.Title, artist, album, durMs, (int)posMs, playing,
                                 art, info?.IsShuffleActive == true, repeat, null);
        }
        catch (Exception ex)
        {
            Log.Warn($"Windows media read failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Album art comes as a stream; save it to a temp file (once per song) so the overlay can load it by URI.</summary>
    private async Task<string?> GetArtAsync(string id, GlobalSystemMediaTransportControlsSessionMediaProperties props)
    {
        if (id == _artId) return _artPath;
        _artId = id; _artPath = null;
        try
        {
            if (props.Thumbnail == null) return null;
            using var ras = await props.Thumbnail.OpenReadAsync();
            using var stream = ras.AsStreamForRead();
            var path = Path.Combine(Path.GetTempPath(), $"museek_art_{(uint)id.GetHashCode():x}.img");
            await using (var fs = File.Create(path)) await stream.CopyToAsync(fs);

            foreach (var old in Directory.GetFiles(Path.GetTempPath(), "museek_art_*.img"))   // keep only the newest
                if (!string.Equals(old, path, StringComparison.OrdinalIgnoreCase))
                    try { File.Delete(old); } catch { }

            _artPath = path;
        }
        catch (Exception ex) { Log.Info($"No album art from media session: {ex.Message}"); }
        return _artPath;
    }

    public Task<List<TrackInfo>> GetQueueAsync(int max = 8) => Task.FromResult(new List<TrackInfo>());

    // ------------------------------------------------------------- controls ----
    private async Task<bool> Run(string name, Func<GlobalSystemMediaTransportControlsSession, Windows.Foundation.IAsyncOperation<bool>> op)
    {
        try
        {
            var s = _session ?? PickSession();
            if (s == null) return false;
            return await op(s);
        }
        catch (Exception ex) { Log.Warn($"{name} failed: {ex.Message}"); return false; }
    }

    public Task<bool> PlayPauseAsync() => Run("play_pause", s => s.TryTogglePlayPauseAsync());
    public Task<bool> NextTrackAsync() => Run("next", s => s.TrySkipNextAsync());
    public Task<bool> PreviousTrackAsync() => Run("previous", s => s.TrySkipPreviousAsync());

    public Task<bool> SeekToAsync(int positionMs) => Run("seek", s =>
    {
        var start = s.GetTimelineProperties().StartTime;
        return s.TryChangePlaybackPositionAsync(start.Ticks + positionMs * TimeSpan.TicksPerMillisecond);
    });

    public Task<bool> ToggleShuffleAsync(bool currentlyOn) => Run("shuffle", s => s.TryChangeShuffleActiveAsync(!currentlyOn));

    public Task<bool> CycleRepeatAsync(string currentMode) => Run("repeat", s => s.TryChangeAutoRepeatModeAsync(currentMode switch
    {
        "off" => MediaPlaybackAutoRepeatMode.List,
        "context" => MediaPlaybackAutoRepeatMode.Track,
        _ => MediaPlaybackAutoRepeatMode.None,
    }));

    // Volume: press Windows' volume keys (changes the system volume, ~2% per press).
    public Task<bool> SetVolumeAsync(int percent) => Task.FromResult(false);
    public Task<bool> VolumeUpAsync(int step = 10) => Task.FromResult(PressVolume(VK_VOLUME_UP, step));
    public Task<bool> VolumeDownAsync(int step = 10) => Task.FromResult(PressVolume(VK_VOLUME_DOWN, step));

    private static bool PressVolume(byte key, int step)
    {
        var presses = Math.Clamp(step / 2, 1, 25);
        for (var i = 0; i < presses; i++)
        {
            keybd_event(key, 0, 0, UIntPtr.Zero);
            keybd_event(key, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
        return true;
    }

    // ---------------------------------------------------- not available here ----
    public Task<(bool ok, string? title)> SearchAndPlayAsync(string query) => Task.FromResult<(bool, string?)>((false, null));
    public Task<(bool ok, string? title)> AddToQueueAsync(string query) => Task.FromResult<(bool, string?)>((false, null));
    public Task<bool> IsTrackSavedAsync(string trackId) => Task.FromResult(false);
    public Task<bool?> ToggleLikeCurrentAsync() => Task.FromResult<bool?>(null);
}
