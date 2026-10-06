using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Museek.Services;

/// <summary>
/// Music chatbot backed by Google's Gemini REST API (free tier key from
/// https://aistudio.google.com/apikey). Provider-specific code is isolated in
/// CallGeminiAsync so it can be swapped out.
/// </summary>
public sealed class ChatbotService
{
    private const string SystemPrompt =
        "You are Museek, a friendly, concise music-knowledge assistant embedded in a desktop overlay. " +
        "You'll be told what track is currently playing (if any). Answer questions about the song, " +
        "artist, album, genre, trivia, or give recommendations. Keep answers short (2-4 sentences) " +
        "since they display in a small chat panel.";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly IMusicSource _spotify;

    public ChatbotService(IMusicSource spotify) => _spotify = spotify;

    private static string ApiKey => SettingsStore.Current.GetGeminiKey();

    public bool IsEnabled => SettingsStore.Current.ChatbotEnabled && ApiKey.Length > 0;

    public async Task<string> AskAsync(string question)
    {
        if (!SettingsStore.Current.ChatbotEnabled)
            return "The chatbot is turned off in Settings. Turn it back on to ask questions.";
        if (ApiKey.Length == 0)
            return "The chatbot isn't configured — add a Gemini API key in Settings.";

        try
        {
            var prompt = await BuildContextAsync() + "\n\nQuestion: " + question;
            var answer = (await CallGeminiAsync(prompt)).Trim();
            return answer.Length == 0 ? "I'm not sure how to answer that." : answer;
        }
        catch (Exception ex)
        {
            Log.Error($"Chatbot request failed: {ex.Message}");
            return "Sorry, I couldn't reach the chatbot service just now.";
        }
    }

    private async Task<string> BuildContextAsync()
    {
        var t = await _spotify.GetCurrentTrackAsync();
        return t == null
            ? "Nothing is currently playing."
            : $"Currently playing: \"{t.Name}\" by {t.Artist}, from the album \"{t.Album}\".";
    }

    /// <summary>Asks Gemini for songs that fit a request ("something chill for studying"). Empty list on any failure.</summary>
    public async Task<List<(string Title, string Artist)>> SuggestSongsAsync(string request, int count)
    {
        var songs = new List<(string, string)>();
        if (!IsEnabled) return songs;
        try
        {
            var what = request.Length == 0 ? "anything a music fan would enjoy right now" : request;
            var prompt = $"Suggest {count} real, well-known songs that fit this request: \"{what}\". " +
                         "Reply with ONLY a JSON array like [{\"title\":\"Song name\",\"artist\":\"Artist name\"}]. No commentary.";
            var raw = (await CallGeminiAsync(prompt, "You are a music curator. You output only valid JSON.", json: true)).Trim();
            raw = raw.Trim('`').Trim();
            if (raw.StartsWith("json", StringComparison.OrdinalIgnoreCase)) raw = raw[4..];

            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return songs;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var title = el.TryGetProperty("title", out var t) ? t.GetString() : null;
                var artist = el.TryGetProperty("artist", out var a2) ? a2.GetString() : null;
                if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(artist))
                    songs.Add((title!.Trim(), artist!.Trim()));
            }
        }
        catch (Exception ex) { Log.Warn($"Song suggestion failed: {ex.Message}"); }
        return songs;
    }

    private static async Task<string> CallGeminiAsync(string prompt, string? system = null, bool json = false)
    {
        var model = SettingsStore.Current.ChatbotModel;
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent";

        var body = new Dictionary<string, object>
        {
            ["systemInstruction"] = new { parts = new[] { new { text = system ?? SystemPrompt } } },
            ["contents"] = new[] { new { role = "user", parts = new[] { new { text = prompt } } } },
        };
        if (json) body["generationConfig"] = new { responseMimeType = "application/json" };

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("x-goog-api-key", ApiKey);

        using var resp = await Http.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var parts = doc.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts");
        var sb = new StringBuilder();
        foreach (var p in parts.EnumerateArray())
            if (p.TryGetProperty("text", out var t)) sb.Append(t.GetString());
        return sb.ToString();
    }
}
