using System;
using System.IO;
using Microsoft.Win32;

namespace Museek.Services;

/// <summary>
/// System-wide EQ presets (bass boost etc.) via the free "Equalizer APO" audio driver.
/// Spotify's Web API has no EQ control, so Museek writes an include-file that Equalizer APO
/// picks up automatically. Applies to ALL audio on the selected playback device, not just Spotify.
/// </summary>
public static class EqualizerService
{
    private const string IncludeFile = "museek_eq.txt";

    private static readonly System.Collections.Generic.Dictionary<string, string> Presets = new()
    {
        ["flat"] = "# Museek: flat\r\n",
        ["bass"] = "Preamp: -6 dB\r\nFilter: ON LSC Fc 100 Hz Gain 8 dB\r\n",
        ["vocal"] = "Preamp: -3 dB\r\nFilter: ON PK Fc 250 Hz Gain -2 dB Q 1\r\nFilter: ON PK Fc 3000 Hz Gain 3 dB Q 1\r\n",
        ["electronic"] = "Preamp: -5 dB\r\nFilter: ON LSC Fc 80 Hz Gain 5 dB\r\nFilter: ON HSC Fc 8000 Hz Gain 4 dB\r\n",
        ["treble"] = "Preamp: -4 dB\r\nFilter: ON HSC Fc 6000 Hz Gain 5 dB\r\n",
    };

    private static string? FindConfigDir()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\EqualizerAPO");
            if (key?.GetValue("InstallPath") is string installPath)
            {
                var dir = Path.Combine(installPath, "config");
                if (Directory.Exists(dir)) return dir;
            }
        }
        catch { /* fall through to default paths */ }

        foreach (var root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        })
        {
            var dir = Path.Combine(root, "EqualizerAPO", "config");
            if (Directory.Exists(dir)) return dir;
        }
        return null;
    }

    public static bool IsInstalled => FindConfigDir() != null;

    public static (bool ok, string message) Apply(string preset)
    {
        if (!Presets.TryGetValue(preset, out var body)) return (false, "Unknown preset.");

        var dir = FindConfigDir();
        if (dir == null)
            return (false, "Equalizer APO not found. Install it (free, sourceforge.net/projects/equalizerapo), " +
                           "enable it for your speakers/headphones in its Configurator, then pick a preset again.");

        try
        {
            File.WriteAllText(Path.Combine(dir, IncludeFile), body);

            var main = Path.Combine(dir, "config.txt");
            var existing = File.Exists(main) ? File.ReadAllText(main) : "";
            if (!existing.Contains("Include: " + IncludeFile, StringComparison.OrdinalIgnoreCase))
                File.AppendAllText(main, (existing.EndsWith("\n") || existing.Length == 0 ? "" : "\r\n") +
                                         "Include: " + IncludeFile + "\r\n");

            Log.Info($"EQ preset applied: {preset}");
            return (true, preset == "flat" ? "EQ is flat (off)." : $"EQ preset applied: {preset}. Affects all audio on your device.");
        }
        catch (UnauthorizedAccessException)
        {
            return (false, "No permission to write Equalizer APO's config folder. Run Museek as administrator once " +
                           "(or give your user write access to " + dir + ").");
        }
        catch (Exception ex)
        {
            Log.Warn($"EQ apply failed: {ex.Message}");
            return (false, "Couldn't apply EQ: " + ex.Message);
        }
    }
}
