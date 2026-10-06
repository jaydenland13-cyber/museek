using System;
using System.Text.RegularExpressions;

namespace Museek.Services;

/// <summary>Recognises "play something chill", "play some jazz music", "mood: rainy day"… (not song titles).</summary>
public static class MoodIntent
{
    private static readonly Regex[] Patterns =
    {
        new(@"^(?:please\s+)?(?:play|put on|queue(?:\s+up)?|can you play|could you play)\s+(?:me\s+)?something\b\s*(?:that(?:'s|\s+is)\s+|like\s+|for\s+)?(.*)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^(?:please\s+)?(?:play|put on|queue(?:\s+up)?)\s+(?:me\s+)?some\s+(.+?)\s+(?:music|songs|tunes|tracks)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^mood\s*[:\-]?\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    };

    /// <param name="request">What kind of music; empty means "anything I'd like".</param>
    public static bool TryParse(string text, out string request)
    {
        request = "";
        var t = text.Trim().TrimEnd('.', '!', '?');
        foreach (var p in Patterns)
        {
            var m = p.Match(t);
            if (!m.Success) continue;
            request = m.Groups[1].Value.Trim();
            return true;
        }
        return false;
    }
}
