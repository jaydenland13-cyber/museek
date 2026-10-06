using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Museek.Services;

/// <summary>What a music source can do. The UI hides controls a source can't support.</summary>
[Flags]
public enum MusicCaps
{
    None = 0,
    Queue = 1,          // can list upcoming tracks / add to queue
    Like = 2,           // can like / unlike the current track
    Search = 4,         // can search and play by name
    Volume = 8,         // can set an absolute volume 0-100
    VolumeKeys = 16,    // can only nudge volume up/down (system volume keys)
    ShuffleRepeat = 32, // shuffle / repeat (best effort)
    Seek = 64,          // can jump to a position
}

/// <summary>A place the now-playing info and controls come from (Spotify, or Windows' media session).</summary>
public interface IMusicSource
{
    string Name { get; }
    /// <summary>One-line status for the footer, e.g. "Connected to Spotify" or "Apple Music · via Windows media".</summary>
    string StatusText { get; }
    bool IsConnected { get; }
    string? AuthError { get; }
    /// <summary>Local sources are cheap to poll, so the overlay refreshes them faster.</summary>
    bool IsLocal { get; }
    MusicCaps Caps { get; }

    /// <summary>Raised (on any thread) when the source knows something changed, so the UI can refresh without polling.</summary>
    event Action? Changed;

    Task ConnectAsync();
    Task<TrackInfo?> GetCurrentTrackAsync();
    Task<List<TrackInfo>> GetQueueAsync(int max = 8);

    Task<bool> PlayPauseAsync();
    Task<bool> NextTrackAsync();
    Task<bool> PreviousTrackAsync();
    Task<bool> SeekToAsync(int positionMs);

    Task<bool> SetVolumeAsync(int percent);
    Task<bool> VolumeUpAsync(int step = 10);
    Task<bool> VolumeDownAsync(int step = 10);

    Task<bool> ToggleShuffleAsync(bool currentlyOn);
    Task<bool> CycleRepeatAsync(string currentMode);

    Task<(bool ok, string? title)> SearchAndPlayAsync(string query);
    Task<(bool ok, string? title)> AddToQueueAsync(string query);

    Task<bool> IsTrackSavedAsync(string trackId);
    Task<bool?> ToggleLikeCurrentAsync();

    /// <summary>A web link for the track, if the source has one.</summary>
    string? TrackUrl(string trackId);
}
