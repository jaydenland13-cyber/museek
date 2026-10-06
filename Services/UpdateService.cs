using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace Museek.Services;

public sealed record UpdateInfo(string Version, string Url, string? Sha256, string Notes);

/// <summary>
/// In-app updater. Checks a GitHub "latest release" (or a small JSON manifest), downloads the new Museek.exe,
/// verifies its SHA-256 when one is published, swaps it in (Windows lets you rename a running exe) and restarts.
/// Only https URLs are accepted. Nothing runs unless the user presses "Install".
/// </summary>
public static class UpdateService
{
    private static readonly HttpClient Http = Create();

    private static HttpClient Create()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Museek-Updater/1.0");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return c;
    }

    public static string EffectiveUrl =>
        string.IsNullOrWhiteSpace(SettingsStore.Current.UpdateCheckUrl) ? AppInfo.DefaultUpdateUrl : SettingsStore.Current.UpdateCheckUrl.Trim();

    // ------------------------------------------------------------------ check ----
    public static async Task<(UpdateInfo? update, string status)> CheckAsync()
    {
        var url = EffectiveUrl;
        if (url.Length == 0) return (null, "This build has no update source configured.");
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return (null, "The update URL must be https.");

        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            var root = doc.RootElement;
            string version, download = "", notes = "";
            string? sha = null;

            if (root.TryGetProperty("tag_name", out var tag))                    // GitHub release
            {
                version = tag.GetString() ?? "";
                if (root.TryGetProperty("body", out var body)) notes = body.GetString() ?? "";
                if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    var exes = assets.EnumerateArray()
                        .Where(a => (a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "").EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    var pick = exes.FirstOrDefault(a => (a.GetProperty("name").GetString() ?? "").Equals("Museek.exe", StringComparison.OrdinalIgnoreCase));
                    if (pick.ValueKind == JsonValueKind.Undefined && exes.Count > 0) pick = exes[0];
                    if (pick.ValueKind != JsonValueKind.Undefined)
                    {
                        download = pick.GetProperty("browser_download_url").GetString() ?? "";
                        if (pick.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String)
                            sha = (dg.GetString() ?? "").Replace("sha256:", "", StringComparison.OrdinalIgnoreCase);
                    }
                }
                if (download.Length == 0) return (null, "The latest release has no .exe attached.");
            }
            else                                                                   // plain JSON manifest
            {
                version = root.GetProperty("version").GetString() ?? "";
                download = root.GetProperty("url").GetString() ?? "";
                if (root.TryGetProperty("sha256", out var h)) sha = h.GetString();
                if (root.TryGetProperty("notes", out var n)) notes = n.GetString() ?? "";
            }

            version = version.Trim().TrimStart('v', 'V');
            if (!IsNewer(version, AppInfo.Version)) return (null, $"You're up to date (v{AppInfo.Version}).");
            if (!download.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return (null, "The download link must be https.");

            if (notes.Length > 600) notes = notes[..600] + "…";
            return (new UpdateInfo(version, download, string.IsNullOrWhiteSpace(sha) ? null : sha.Trim(), notes), $"Version {version} is available.");
        }
        catch (Exception ex)
        {
            Log.Info($"Update check failed: {ex.Message}");
            return (null, "Couldn't check for updates: " + ex.Message);
        }
    }

    private static int[] ParseVersion(string v) =>
        v.Trim().Split('.').Select(p => new string(p.TakeWhile(char.IsDigit).ToArray()))
         .Select(d => d.Length == 0 ? 0 : int.Parse(d)).ToArray();

    private static bool IsNewer(string latest, string current)
    {
        var a = ParseVersion(latest); var b = ParseVersion(current);
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0; var y = i < b.Length ? b[i] : 0;
            if (x != y) return x > y;
        }
        return false;
    }

    // ---------------------------------------------------------------- install ----
    /// <summary>Downloads, verifies and swaps in the new exe, then starts it. The caller should quit the app when ok == true.</summary>
    public static async Task<(bool ok, string message)> DownloadAndInstallAsync(UpdateInfo info, IProgress<int>? progress)
    {
        var exe = Environment.ProcessPath;
        if (exe == null || exe.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            return (false, "Updating works from the published Museek.exe (not when running from source).");

        var dir = Path.GetDirectoryName(exe)!;
        var tmp = Path.Combine(dir, "Museek.update.tmp");
        var old = exe + ".old";

        try
        {
            using (var resp = await Http.GetAsync(info.Url, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? -1;
                await using var src = await resp.Content.ReadAsStreamAsync();
                await using var dst = File.Create(tmp);
                var buf = new byte[81920];
                long done = 0; int last = -1, n;
                while ((n = await src.ReadAsync(buf)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n));
                    done += n;
                    if (total > 0)
                    {
                        var pct = (int)(done * 100 / total);
                        if (pct != last) { last = pct; progress?.Report(pct); }
                    }
                }
            }

            // sanity: looks like a Windows program and is not tiny
            var fi = new FileInfo(tmp);
            using (var fs = File.OpenRead(tmp))
                if (fi.Length < 1_000_000 || fs.ReadByte() != 'M' || fs.ReadByte() != 'Z')
                { File.Delete(tmp); return (false, "The downloaded file doesn't look like Museek.exe — update cancelled."); }

            if (!string.IsNullOrEmpty(info.Sha256))
            {
                string actual;
                using (var fs = File.OpenRead(tmp)) actual = Convert.ToHexString(await SHA256.HashDataAsync(fs));
                if (!actual.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
                { File.Delete(tmp); return (false, "The download failed its integrity check (SHA-256 mismatch) — update cancelled."); }
            }

            try { if (File.Exists(old)) File.Delete(old); } catch { }
            File.Move(exe, old);                                       // a running exe can be renamed, not overwritten
            try { File.Move(tmp, exe); }
            catch { if (!File.Exists(exe) && File.Exists(old)) File.Move(old, exe); throw; }   // roll back

            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Arguments = $"--wait-pid {Environment.ProcessId}" });
            Log.Info($"Updated to {info.Version}; restarting.");
            return (true, "Update installed — restarting…");
        }
        catch (UnauthorizedAccessException)
        {
            TryDelete(tmp);
            return (false, "Museek can't write to its own folder. Download the new version manually from your update page.");
        }
        catch (Exception ex)
        {
            TryDelete(tmp);
            Log.Warn($"Update failed: {ex.Message}");
            return (false, "Update failed: " + ex.Message);
        }
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    /// <summary>Removes the previous exe left behind by the last update.</summary>
    public static void CleanupOldVersion()
    {
        var exe = Environment.ProcessPath;
        if (exe == null) return;
        TryDelete(exe + ".old");
    }
}
