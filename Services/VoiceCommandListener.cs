using System;
using System.Speech.Recognition;
using System.Windows;
using System.Windows.Threading;

namespace Museek.Services;

/// <summary>
/// Voice commands via Windows' built-in speech recognition.
///
/// PUSH-TO-TALK (default): nothing is listening until you press the hotkey / mic button.
///   The speech engine is created on first use and thrown away after 30 s idle, so it costs
///   ~0 CPU/RAM most of the time and can't be triggered by the music playing in the room.
///   Commands + free text ("play &lt;song&gt;", "search ...", "ask ...") are available.
///
/// ALWAYS-LISTENING (optional): needs the wake word first, uses only the fixed command list
///   (no free text), a high confidence threshold and a cool-down between commands.
/// </summary>
public sealed class VoiceCommandListener : IDisposable
{
    private static readonly string[] FixedCommands =
    {
        "play", "pause", "play pause", "toggle",
        "next", "skip", "next track", "skip track",
        "previous", "back", "previous track", "go back",
        "volume up", "louder", "turn it up",
        "volume down", "quieter", "turn it down",
        "like", "save this", "like this song",
        "show", "show overlay", "hide", "hide overlay",
        "what's playing", "what is playing",
        "stop talking", "be quiet",
    };

    private static readonly string[] FreeformStarters = { "play", "search", "ask" };

    private const float AlwaysThreshold = 0.80f;   // always-listening: very strict
    private const float PttCommandThreshold = 0.60f;
    private const float PttFreeformThreshold = 0.50f;
    private static readonly TimeSpan Cooldown = TimeSpan.FromMilliseconds(1500);

    private readonly Action<string> _onCommand;
    private readonly HotkeyManager _hotkeys;
    private readonly DispatcherTimer _idle = new() { Interval = TimeSpan.FromSeconds(30) };
    private SpeechRecognitionEngine? _engine;
    private int _pttHotkeyId = -1;
    private bool _pttBusy;
    private DateTime _lastCommandUtc = DateTime.MinValue;

    /// <summary>Raised on the UI thread with what was heard (or a short status note).</summary>
    public event Action<string>? Heard;

    public bool IsRunning { get; private set; }

    public VoiceCommandListener(Action<string> onCommand, HotkeyManager hotkeys)
    {
        _onCommand = onCommand;
        _hotkeys = hotkeys;
        _idle.Tick += (_, _) => { _idle.Stop(); if (!_pttBusy) DisposeEngine(); };
    }

    public void Start()
    {
        var s = SettingsStore.Current;
        if (s.PushToTalkEnabled)
        {
            _pttHotkeyId = _hotkeys.Register(s.PushToTalkKey, PushToTalk);
            IsRunning = true;
            Log.Info($"Push-to-talk ready — press '{s.PushToTalkKey}' (or the mic button) and speak a command.");
            return;
        }

        if (!EnsureEngine()) return;
        try { _engine!.RecognizeAsync(RecognizeMode.Multiple); IsRunning = true; }
        catch (Exception ex) { Log.Warn($"Could not start listening: {ex.Message}"); UiNote("Voice unavailable"); return; }
        Log.Info($"Always-listening started (wake word: '{s.WakeWord}').");
    }

    private bool EnsureEngine()
    {
        if (_engine != null) return true;
        var s = SettingsStore.Current;
        try
        {
            var wake = s.PushToTalkEnabled ? null : s.WakeWord.Trim().ToLowerInvariant();
            var engine = new SpeechRecognitionEngine();
            engine.LoadGrammar(BuildCommandGrammar(wake));
            if (s.PushToTalkEnabled) engine.LoadGrammar(BuildFreeformGrammar());   // free text only on demand
            engine.SetInputToDefaultAudioDevice();
            engine.InitialSilenceTimeout = TimeSpan.FromSeconds(5);
            engine.BabbleTimeout = TimeSpan.FromSeconds(6);
            engine.SpeechRecognized += OnRecognized;
            engine.SpeechRecognitionRejected += (_, _) =>
            {
                if (SettingsStore.Current.PushToTalkEnabled) UiNote("Didn't catch that — try again");
            };
            engine.RecognizeCompleted += (_, _) =>
            {
                _pttBusy = false;
                Application.Current?.Dispatcher.BeginInvoke(() => { _idle.Stop(); _idle.Start(); });
            };
            _engine = engine;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"No microphone / speech engine, voice commands disabled: {ex.Message}");
            UiNote("Voice unavailable — no microphone or speech engine");
            IsRunning = false;
            return false;
        }
    }

    private static Grammar BuildCommandGrammar(string? wake)
    {
        var gb = new GrammarBuilder();
        if (!string.IsNullOrEmpty(wake)) gb.Append(wake);
        gb.Append(new Choices(FixedCommands));
        return new Grammar(gb) { Name = "commands" };
    }

    private static Grammar BuildFreeformGrammar()
    {
        var gb = new GrammarBuilder();
        gb.Append(new Choices(FreeformStarters));
        gb.AppendDictation();
        return new Grammar(gb) { Name = "freeform" };
    }

    /// <summary>Listen for ONE command (hotkey / mic button, push-to-talk mode).</summary>
    public void PushToTalk()
    {
        if (_pttBusy) return;
        if (!EnsureEngine()) return;
        _idle.Stop();
        _pttBusy = true;
        UiNote("Listening… say a command");
        try { _engine!.RecognizeAsync(RecognizeMode.Single); }
        catch (Exception ex) { _pttBusy = false; Log.Warn($"Push-to-talk error: {ex.Message}"); }
    }

    private void OnRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        var ptt = SettingsStore.Current.PushToTalkEnabled;
        var threshold = !ptt ? AlwaysThreshold
                       : e.Result.Grammar?.Name == "commands" ? PttCommandThreshold : PttFreeformThreshold;
        if (e.Result.Confidence < threshold) { if (ptt) UiNote("Didn't catch that — try again"); return; }

        var text = e.Result.Text.ToLowerInvariant().Trim();
        if (text.Length == 0) return;

        var now = DateTime.UtcNow;
        if (now - _lastCommandUtc < Cooldown) return;           // ignore rapid repeats / echoes
        Log.Info($"Heard: '{text}' (confidence {e.Result.Confidence:0.00})");

        if (ptt) { _lastCommandUtc = now; Dispatch(text); return; }

        var wake = SettingsStore.Current.WakeWord.ToLowerInvariant().Trim();
        if (!text.StartsWith(wake, StringComparison.Ordinal)) return;
        var command = text[wake.Length..].Trim(' ', ',', '.');
        if (command.Length == 0) return;

        _lastCommandUtc = now;
        Dispatch(command);
    }

    private void Dispatch(string command) =>
        Application.Current?.Dispatcher.BeginInvoke(() => { Heard?.Invoke($"Heard: “{command}”"); _onCommand(command); });

    private void UiNote(string note) =>
        Application.Current?.Dispatcher.BeginInvoke(() => Heard?.Invoke(note));

    private void DisposeEngine()
    {
        try { _engine?.RecognizeAsyncCancel(); _engine?.Dispose(); } catch { }
        _engine = null;
        MemoryTrim.RunSoon(3);
    }

    public void Stop()
    {
        _idle.Stop();
        if (_pttHotkeyId >= 0) _hotkeys.Unregister(_pttHotkeyId);
        _pttHotkeyId = -1;
        DisposeEngine();
        IsRunning = false;
    }

    public void Dispose() => Stop();
}
