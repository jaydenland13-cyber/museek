using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Museek.Services;

/// <summary>Synced (LRC) lyrics from lrclib.net — free, no API key.</summary>
public static class LyricsService
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly Regex LrcLine = new(@"\[(\d+):(\d+(?:\.\d+)?)\](.*)", RegexOptions.Compiled);

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Museek/1.0");
        return c;
    }

    public static List<(double Seconds, string Text)> ParseLrc(string lrc)
    {
        var lines = new List<(double, string)>();
        foreach (var raw in lrc.Split('\n'))
        {
            var m = LrcLine.Match(raw.Trim());
            if (!m.Success) continue;
            var minutes = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var seconds = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            lines.Add((minutes * 60 + seconds, m.Groups[3].Value.Trim()));
        }
        lines.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return lines;
    }

    /// <summary>Returns (seconds, line) pairs sorted by time, or null if none found.</summary>
    public static async Task<List<(double Seconds, string Text)>?> FetchSyncedAsync(
        string track, string artist, int durationMs)
    {
        try
        {
            // 1) exact lookup (needs a duration to be precise)
            if (durationMs > 0)
            {
                var url = "https://lrclib.net/api/get" +
                          $"?track_name={Uri.EscapeDataString(track)}" +
                          $"&artist_name={Uri.EscapeDataString(artist)}" +
                          $"&duration={(int)Math.Round(durationMs / 1000.0)}";
                using var resp = await Http.GetAsync(url);
                if ((int)resp.StatusCode == 200)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    if (doc.RootElement.TryGetProperty("syncedLyrics", out var el) && el.ValueKind == JsonValueKind.String)
                    {
                        var parsed = ParseLrc(el.GetString() ?? "");
                        if (parsed.Count > 0) return parsed;
                    }
                }
            }

            // 2) search (used when the player doesn't report a duration, or the exact lookup missed)
            var surl = "https://lrclib.net/api/search" +
                       $"?track_name={Uri.EscapeDataString(track)}&artist_name={Uri.EscapeDataString(artist)}";
            using var sresp = await Http.GetAsync(surl);
            if ((int)sresp.StatusCode != 200) return null;
            using var sdoc = JsonDocument.Parse(await sresp.Content.ReadAsStringAsync());
            if (sdoc.RootElement.ValueKind != JsonValueKind.Array) return null;

            string? best = null;
            var bestDiff = double.MaxValue;
            foreach (var item in sdoc.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("syncedLyrics", out var sl) || sl.ValueKind != JsonValueKind.String) continue;
                var text = sl.GetString();
                if (string.IsNullOrWhiteSpace(text)) continue;
                var diff = durationMs > 0 && item.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
                    ? Math.Abs(d.GetDouble() - durationMs / 1000.0) : 0;
                if (diff < bestDiff) { bestDiff = diff; best = text; }
            }
            if (best == null) return null;
            var lines = ParseLrc(best);
            return lines.Count == 0 ? null : lines;
        }
        catch (Exception ex)
        {
            Log.Info($"Lyrics lookup failed for '{track}' by {artist}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Index of the line that should be showing now, or -1 before the first line.</summary>
    public static int CurrentLineIndex(List<(double Seconds, string Text)> lines, double positionSeconds)
    {
        var idx = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Seconds <= positionSeconds) idx = i; else break;
        }
        return idx;
    }
}
