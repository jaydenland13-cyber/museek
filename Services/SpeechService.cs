using System;
using System.Speech.Synthesis;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;

namespace Museek.Services;

/// <summary>Reads text aloud with Windows' built-in voices. The synthesizer is created on demand and freed after 20 s idle.</summary>
public static class SpeechService
{
    private static SpeechSynthesizer? _synth;
    private static DispatcherTimer? _idle;

    public static void Speak(string text)
    {
        var clean = Clean(text);
        if (clean.Length == 0) return;
        try
        {
            _synth ??= CreateSynth();
            _synth.SpeakAsyncCancelAll();
            _idle?.Stop();
            _synth.SpeakAsync(clean);
        }
        catch (Exception ex) { Log.Info($"Speech failed: {ex.Message}"); }
    }

    public static void Stop()
    {
        try { _synth?.SpeakAsyncCancelAll(); } catch { }
    }

    private static SpeechSynthesizer CreateSynth()
    {
        var s = new SpeechSynthesizer { Volume = 100 };
        s.SpeakCompleted += (_, _) => Application.Current?.Dispatcher.BeginInvoke(new Action(StartIdleTimer));
        return s;
    }

    private static void StartIdleTimer()
    {
        _idle ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _idle.Tick -= OnIdle;
        _idle.Tick += OnIdle;
        _idle.Stop();
        _idle.Start();
    }

    private static void OnIdle(object? sender, EventArgs e)
    {
        _idle?.Stop();
        try { _synth?.Dispose(); } catch { }
        _synth = null;
        MemoryTrim.RunSoon(3);
    }

    /// <summary>Strip markdown, links and emoji so the voice doesn't read symbols out loud.</summary>
    private static string Clean(string text)
    {
        var t = Regex.Replace(text, @"https?://\S+", " ");
        t = Regex.Replace(t, @"[*_`#>~|]", "");
        t = Regex.Replace(t, @"[\p{Cs}\p{So}]", "");
        t = Regex.Replace(t, @"\s+", " ");
        return t.Trim();
    }
}
