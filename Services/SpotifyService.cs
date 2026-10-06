using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SpotifyAPI.Web;
using SpotifyAPI.Web.Auth;

namespace Museek.Services;

public sealed record TrackInfo(
    string Id, string Name, string Artist, string Album,
    int DurationMs, int ProgressMs, bool IsPlaying,
    string? AlbumArtUrl, bool ShuffleState, string RepeatState, int? VolumePercent = null);

/// <summary>
/// Thin wrapper over SpotifyAPI.Web. Like the original, every method fails soft
/// (returns null/false + logs) so UI/hotkey callers never crash on an API hiccup.
///
/// Uses the PKCE flow, so NO client secret is needed or stored.
/// </summary>
public sealed class SpotifyService : IMusicSource
{
    private const string RedirectUri = "http://127.0.0.1:8888/callback";

    // NOTE: the original app requested only 4 scopes but also called the
    // saved-tracks endpoints (like button), which require the two library scopes.
    private static readonly string[] RequiredScopes =
    {
        "user-read-currently-playing",
        "user-read-playback-state",
        "user-modify-playback-state",
        "user-read-recently-played",
        "user-library-read",
        "user-library-modify",
    };

    private static string CachePath => Path.Combine(Log.BaseDir, ".spotify_cache");

    private SpotifyClient? _sp;

    public event Action? Changed { add { } remove { } }   // Spotify has no push channel: it is polled
    public string Name => "Spotify";
    public bool IsLocal => false;
    public MusicCaps Caps => MusicCaps.Queue | MusicCaps.Like | MusicCaps.Search | MusicCaps.Volume | MusicCaps.ShuffleRepeat | MusicCaps.Seek;
    public string StatusText => IsConnected ? "Connected to Spotify" : "Not connected — check Settings";
    public string? TrackUrl(string trackId) => $"https://open.spotify.com/track/{trackId}";

    public bool IsConnected => _sp != null;
    public string? AuthError { get; private set; }

    // -------------------------------------------------------------- auth ----
    public Task ConnectAsync() => ConnectAsync(interactive: true, CancellationToken.None);

    /// <param name="interactive">false = only reuse a saved login (never opens the browser); true = sign in if needed.</param>
    public async Task ConnectAsync(bool interactive, CancellationToken ct)
    {
        var clientId = SettingsStore.Current.SpotifyClientId.Trim();
        if (clientId.Length == 0)
        {
            AuthError = "Spotify Client ID is empty. Paste the Client ID of your Spotify Developer app.";
            Log.Error(AuthError);
            return;
        }

        try
        {
            PKCETokenResponse? token = LoadCachedToken();
            if (token == null || !HasAllScopes(token))
            {
                if (!interactive) { AuthError = "Not signed in."; return; }
                token = await LoginInteractiveAsync(clientId, ct);
                SaveToken(token);
            }

            var authenticator = new PKCEAuthenticator(clientId, token);
            authenticator.TokenRefreshed += (_, t) => SaveToken(t);

            var config = SpotifyClientConfig.CreateDefault().WithAuthenticator(authenticator);
            _sp = new SpotifyClient(config);
            AuthError = null;
            Log.Info("Connected to Spotify.");
            await LoadUserAsync();
        }
        catch (OperationCanceledException)
        {
            _sp = null;
            AuthError = "Sign-in cancelled.";
        }
        catch (Exception ex)
        {
            _sp = null;
            AuthError = ex.Message;
            Log.Error($"Spotify auth failed: {ex.Message}");
        }
    }

    /// <summary>Display name of the signed-in account (best effort — shown on the welcome screen).</summary>
    public string? UserName { get; private set; }

    public async Task LoadUserAsync()
    {
        if (_sp == null) return;
        try
        {
            var me = await _sp.UserProfile.Current();
            UserName = string.IsNullOrWhiteSpace(me.DisplayName) ? me.Id : me.DisplayName;
        }
        catch (Exception ex) { Log.Info($"Could not read Spotify profile: {ex.Message}"); }
    }

    /// <summary>Forget the saved login (the next sign-in will ask Spotify again).</summary>
    public void SignOut()
    {
        _sp = null;
        UserName = null;
        AuthError = null;
        try { if (File.Exists(CachePath)) File.Delete(CachePath); } catch { }
        Log.Info("Signed out of Spotify.");
    }

    private static bool HasAllScopes(PKCETokenResponse t)
    {
        var granted = (t.Scope ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return RequiredScopes.All(s => granted.Contains(s));
    }

    private static PKCETokenResponse? LoadCachedToken()
    {
        try
        {
            if (!File.Exists(CachePath)) return null;
            return JsonSerializer.Deserialize<PKCETokenResponse>(File.ReadAllText(CachePath));
        }
        catch { return null; }
    }

    private static void SaveToken(PKCETokenResponse t)
    {
        try { File.WriteAllText(CachePath, JsonSerializer.Serialize(t)); }
        catch (Exception ex) { Log.Warn($"Could not write Spotify token cache: {ex.Message}"); }
    }

    private static async Task<PKCETokenResponse> LoginInteractiveAsync(string clientId, CancellationToken ct)
    {
        var (verifier, challenge) = PKCEUtil.GenerateCodes();
        var redirect = new Uri(RedirectUri);
        var codeSource = new TaskCompletionSource<string>();

        var server = new EmbedIOAuthServer(redirect, 8888);
        server.AuthorizationCodeReceived += (_, response) =>
        {
            codeSource.TrySetResult(response.Code);
            return Task.CompletedTask;
        };
        server.ErrorReceived += (_, error, _) =>
        {
            codeSource.TrySetException(new Exception($"Spotify login error: {error}"));
            return Task.CompletedTask;
        };

        await server.Start();
        try
        {
            var request = new LoginRequest(redirect, clientId, LoginRequest.ResponseType.Code)
            {
                CodeChallenge = challenge,
                CodeChallengeMethod = "S256",
                Scope = RequiredScopes,
            };
            BrowserUtil.Open(request.ToUri());

            var code = await codeSource.Task.WaitAsync(TimeSpan.FromMinutes(3), ct);
            return await new OAuthClient().RequestToken(
                new PKCETokenRequest(clientId, code, redirect, verifier));
        }
        finally
        {
            await server.Stop();
        }
    }

    // ------------------------------------------------------------- reads ----
    public async Task<TrackInfo?> GetCurrentTrackAsync()
    {
        if (_sp == null) return null;
        try
        {
            var ctx = await _sp.Player.GetCurrentPlayback();
            if (ctx?.Item is not FullTrack t) return null;
            return new TrackInfo(
                t.Id, t.Name,
                string.Join(", ", t.Artists.Select(a => a.Name)),
                t.Album?.Name ?? "",
                t.DurationMs,
                Convert.ToInt32(ctx.ProgressMs),
                ctx.IsPlaying,
                t.Album?.Images?.FirstOrDefault()?.Url,
                ctx.ShuffleState,
                string.IsNullOrEmpty(ctx.RepeatState) ? "off" : ctx.RepeatState,
                ctx.Device?.VolumePercent);
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to fetch current track: {ex.Message}");
            return null;
        }
    }

    /// <summary>Best-effort peek at the next queued track.</summary>
    public async Task<TrackInfo?> GetNextTrackAsync()
    {
        if (_sp == null) return null;
        try
        {
            var q = await _sp.Player.GetQueue();
            if (q?.Queue?.FirstOrDefault() is not FullTrack t) return null;
            return new TrackInfo(
                t.Id, t.Name,
                string.Join(", ", t.Artists.Select(a => a.Name)),
                t.Album?.Name ?? "",
                t.DurationMs, 0, false,
                t.Album?.Images?.FirstOrDefault()?.Url, false, "off");
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to fetch queue: {ex.Message}");
            return null;
        }
    }


    /// <summary>Upcoming tracks in the queue (up to <paramref name="max"/>).</summary>
    public async Task<List<TrackInfo>> GetQueueAsync(int max = 8)
    {
        var result = new List<TrackInfo>();
        if (_sp == null) return result;
        try
        {
            var q = await _sp.Player.GetQueue();
            foreach (var item in q?.Queue?.OfType<FullTrack>().Take(max) ?? Enumerable.Empty<FullTrack>())
                result.Add(new TrackInfo(
                    item.Id, item.Name,
                    string.Join(", ", item.Artists.Select(a => a.Name)),
                    item.Album?.Name ?? "", item.DurationMs, 0, false,
                    item.Album?.Images?.LastOrDefault()?.Url ?? item.Album?.Images?.FirstOrDefault()?.Url,
                    false, "off"));
        }
        catch (Exception ex) { Log.Warn($"Failed to fetch queue: {ex.Message}"); }
        return result;
    }

    public Task<bool> SeekToAsync(int positionMs) =>
        Safe("seek", () => _sp!.Player.SeekTo(new PlayerSeekToRequest(Math.Max(0, positionMs))));

    /// <summary>Searches for a track and appends it to the playback queue.</summary>
    public async Task<(bool ok, string? title)> AddToQueueAsync(string query)
    {
        if (_sp == null || string.IsNullOrWhiteSpace(query)) return (false, null);
        try
        {
            var res = await _sp.Search.Item(new SearchRequest(SearchRequest.Types.Track, query.Trim()) { Limit = 1 });
            var t = res.Tracks?.Items?.FirstOrDefault();
            if (t == null) return (false, null);
            await _sp.Player.AddToQueue(new PlayerAddToQueueRequest(t.Uri));
            return (true, $"{t.Name} — {string.Join(", ", t.Artists.Select(a => a.Name))}");
        }
        catch (Exception ex) { Log.Warn($"add_to_queue failed: {ex.Message}"); return (false, null); }
    }

    /// <summary>Current device volume 0-100, or null if unavailable.</summary>
    public async Task<int?> GetVolumeAsync()
    {
        if (_sp == null) return null;
        try
        {
            var ctx = await _sp.Player.GetCurrentPlayback();
            return ctx?.Device?.VolumePercent;
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to fetch volume: {ex.Message}");
            return null;
        }
    }

    // ---------------------------------------------------------- controls ----
    public async Task<bool> PlayPauseAsync()
    {
        if (_sp == null) return false;
        try
        {
            var ctx = await _sp.Player.GetCurrentPlayback();
            if (ctx?.IsPlaying == true) return await _sp.Player.PausePlayback();
            return await _sp.Player.ResumePlayback(new PlayerResumePlaybackRequest());
        }
        catch (Exception ex) { Log.Warn($"play_pause failed: {ex.Message}"); return false; }
    }

    public Task<bool> NextTrackAsync() => Safe("next_track", () => _sp!.Player.SkipNext());
    public Task<bool> PreviousTrackAsync() => Safe("previous_track", () => _sp!.Player.SkipPrevious());

    public Task<bool> SetVolumeAsync(int percent) =>
        Safe("set_volume", () => _sp!.Player.SetVolume(new PlayerVolumeRequest(Math.Clamp(percent, 0, 100))));

    public async Task<bool> VolumeUpAsync(int step = 10) => await NudgeVolume(+step, "volume_up");
    public async Task<bool> VolumeDownAsync(int step = 10) => await NudgeVolume(-step, "volume_down");

    private async Task<bool> NudgeVolume(int delta, string name)
    {
        var cur = await GetVolumeAsync();
        if (cur == null)
        {
            Log.Warn($"{name}: couldn't read current volume (device may not support it).");
            return false;
        }
        return await SetVolumeAsync(cur.Value + delta);
    }

    public Task<bool> SetShuffleAsync(bool enabled) =>
        Safe("shuffle", () => _sp!.Player.SetShuffle(new PlayerShuffleRequest(enabled)));

    public Task<bool> ToggleShuffleAsync(bool currentlyOn) => SetShuffleAsync(!currentlyOn);

    /// <param name="mode">"off", "context" (repeat playlist/album) or "track".</param>
    public Task<bool> SetRepeatAsync(string mode)
    {
        var state = mode switch
        {
            "track" => PlayerSetRepeatRequest.State.Track,
            "context" => PlayerSetRepeatRequest.State.Context,
            _ => PlayerSetRepeatRequest.State.Off,
        };
        return Safe("repeat", () => _sp!.Player.SetRepeat(new PlayerSetRepeatRequest(state)));
    }

    public Task<bool> CycleRepeatAsync(string currentMode)
    {
        string[] modes = { "off", "context", "track" };
        var i = Array.IndexOf(modes, currentMode);
        return SetRepeatAsync(modes[(i < 0 ? 0 : i + 1) % modes.Length]);
    }

    /// <summary>Searches by free-text query and plays the first result.</summary>
    public async Task<(bool ok, string? title)> SearchAndPlayAsync(string query)
    {
        if (_sp == null || string.IsNullOrWhiteSpace(query)) return (false, null);
        try
        {
            var res = await _sp.Search.Item(new SearchRequest(SearchRequest.Types.Track, query.Trim()) { Limit = 1 });
            var t = res.Tracks?.Items?.FirstOrDefault();
            if (t == null)
            {
                Log.Info($"Search & play: no results for '{query}'");
                return (false, null);
            }
            await _sp.Player.ResumePlayback(new PlayerResumePlaybackRequest { Uris = new List<string> { t.Uri } });
            var title = $"{t.Name} by {string.Join(", ", t.Artists.Select(a => a.Name))}";
            Log.Info($"Playing '{title}' (searched: '{query}')");
            return (true, title);
        }
        catch (Exception ex)
        {
            Log.Warn($"search_and_play failed: {ex.Message}");
            return (false, null);
        }
    }

    // ------------------------------------------------------------- likes ----
    public async Task<bool> IsTrackSavedAsync(string trackId)
    {
        if (_sp == null) return false;
        try
        {
            var r = await _sp.Library.CheckTracks(new LibraryCheckTracksRequest(new List<string> { trackId }));
            return r.Count > 0 && r[0];
        }
        catch (Exception ex) { Log.Warn($"is_track_saved failed: {ex.Message}"); return false; }
    }

    public Task<bool> SaveTrackAsync(string id) =>
        Safe("save_track", () => _sp!.Library.SaveTracks(new LibrarySaveTracksRequest(new List<string> { id })));

    public Task<bool> UnsaveTrackAsync(string id) =>
        Safe("unsave_track", () => _sp!.Library.RemoveTracks(new LibraryRemoveTracksRequest(new List<string> { id })));

    /// <summary>Returns new liked state (true=now liked), or null on failure.</summary>
    public async Task<bool?> ToggleLikeCurrentAsync()
    {
        var cur = await GetCurrentTrackAsync();
        if (cur == null) return null;
        if (await IsTrackSavedAsync(cur.Id))
            return await UnsaveTrackAsync(cur.Id) ? false : null;
        return await SaveTrackAsync(cur.Id) ? true : null;
    }

    // ----------------------------------------------------------- helpers ----
    private async Task<bool> Safe(string name, Func<Task> call)
    {
        if (_sp == null) return false;
        try { await call(); return true; }
        catch (Exception ex) { Log.Warn($"{name} failed: {ex.Message}"); return false; }
    }

    private async Task<bool> Safe(string name, Func<Task<bool>> call)
    {
        if (_sp == null) return false;
        try { return await call(); }
        catch (Exception ex) { Log.Warn($"{name} failed: {ex.Message}"); return false; }
    }
}
