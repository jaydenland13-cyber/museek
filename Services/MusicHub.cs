using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Museek.Services;

/// <summary>
/// The one music source the rest of the app talks to. It forwards every call to Spotify or to the Windows media
/// session, depending on the "Music source" setting, so switching sources works live.
/// </summary>
public sealed class MusicHub : IMusicSource
{
    private readonly SpotifyService _spotify;
    private readonly WindowsMediaService _windows;

    public MusicHub(SpotifyService spotify, WindowsMediaService windows)
    {
        _spotify = spotify;
        _windows = windows;
        _spotify.Changed += () => { if (Active == _spotify) Changed?.Invoke(); };
        _windows.Changed += () => { if (Active == _windows) Changed?.Invoke(); };
    }

    public event System.Action? Changed;

    private IMusicSource Active => SettingsStore.Current.MusicSource == "windows" ? _windows : _spotify;

    public string Name => Active.Name;
    public string StatusText => Active.StatusText;
    public bool IsConnected => Active.IsConnected;
    public string? AuthError => Active.AuthError;
    public bool IsLocal => Active.IsLocal;
    public MusicCaps Caps => Active.Caps;

    public Task ConnectAsync() => Active.ConnectAsync();

    // ---- sign-in helpers used by the welcome screen --------------------------------------
    public bool SpotifyConnected => _spotify.IsConnected;
    public string? UserName => _spotify.UserName;
    public string? SpotifyError => _spotify.AuthError;

    /// <summary>Reuse a saved login without opening anything (Spotify) or just connect (Windows media).</summary>
    public async Task TryResumeAsync()
    {
        if (SettingsStore.Current.MusicSource == "windows") await _windows.ConnectAsync();
        else await _spotify.ConnectAsync(false, System.Threading.CancellationToken.None);
    }

    /// <summary>Interactive Spotify sign-in (opens the browser). Returns true when signed in.</summary>
    public async Task<bool> SignInSpotifyAsync(System.Threading.CancellationToken ct)
    {
        await _spotify.ConnectAsync(true, ct);
        return _spotify.IsConnected;
    }

    public void SignOutSpotify() => _spotify.SignOut();
    /// <summary>Connects using what is already available (a saved Spotify login, or Windows media). Never opens the browser.</summary>
    public async Task ConnectIfNeededAsync() { if (!Active.IsConnected) await TryResumeAsync(); }

    public Task<TrackInfo?> GetCurrentTrackAsync() => Active.GetCurrentTrackAsync();
    public Task<List<TrackInfo>> GetQueueAsync(int max = 8) => Active.GetQueueAsync(max);
    public Task<bool> PlayPauseAsync() => Active.PlayPauseAsync();
    public Task<bool> NextTrackAsync() => Active.NextTrackAsync();
    public Task<bool> PreviousTrackAsync() => Active.PreviousTrackAsync();
    public Task<bool> SeekToAsync(int positionMs) => Active.SeekToAsync(positionMs);
    public Task<bool> SetVolumeAsync(int percent) => Active.SetVolumeAsync(percent);
    public Task<bool> VolumeUpAsync(int step = 10) => Active.VolumeUpAsync(step);
    public Task<bool> VolumeDownAsync(int step = 10) => Active.VolumeDownAsync(step);
    public Task<bool> ToggleShuffleAsync(bool currentlyOn) => Active.ToggleShuffleAsync(currentlyOn);
    public Task<bool> CycleRepeatAsync(string currentMode) => Active.CycleRepeatAsync(currentMode);
    public Task<(bool ok, string? title)> SearchAndPlayAsync(string query) => Active.SearchAndPlayAsync(query);
    public Task<(bool ok, string? title)> AddToQueueAsync(string query) => Active.AddToQueueAsync(query);
    public Task<bool> IsTrackSavedAsync(string trackId) => Active.IsTrackSavedAsync(trackId);
    public Task<bool?> ToggleLikeCurrentAsync() => Active.ToggleLikeCurrentAsync();
    public string? TrackUrl(string trackId) => Active.TrackUrl(trackId);
}
