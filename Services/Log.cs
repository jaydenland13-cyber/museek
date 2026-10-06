using System;
using System.IO;

namespace Museek.Services;

/// <summary>
/// Logs to museek_log.txt next to the exe (overwritten each run), like the original.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _path;

    /// <summary>
    /// Where settings.json, the Spotify token cache and the logs live: next to the exe when that folder is writable
    /// (portable), otherwise %AppData%\Museek (e.g. when the exe sits in Program Files).
    /// </summary>
    public static string BaseDir { get; } = ResolveBaseDir();

    private static string ResolveBaseDir()
    {
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        try
        {
            var probe = Path.Combine(exeDir, ".museek_write_test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return exeDir;
        }
        catch
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Museek");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static void Init()
    {
        _path = Path.Combine(BaseDir, "museek_log.txt");
        try { File.WriteAllText(_path, ""); } catch { /* read-only folder etc. */ }
    }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARNING", msg);
    public static void Error(string msg) => Write("ERROR", msg);

    private static void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss} [{level}] museek: {msg}";
        System.Diagnostics.Debug.WriteLine(line);
        if (_path == null) return;
        lock (Gate)
        {
            try { File.AppendAllText(_path, line + Environment.NewLine); } catch { }
        }
    }
}
