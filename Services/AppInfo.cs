using System;
using System.Reflection;

namespace Museek.Services;

public static class AppInfo
{
    /// <summary>
    /// Where the updater looks for new versions. Before you publish, set this to your GitHub repo's latest-release API URL, e.g.
    ///   https://api.github.com/repos/jaydenland13-cyber/Museek/releases/latest
    /// (attach Museek.exe to each release), or to a small JSON file: {"version":"1.1.0","url":"https://.../Museek.exe","sha256":"...","notes":"..."}.
    /// Leave empty to disable updates. The user can also override it with "UpdateCheckUrl" in settings.json.
    /// </summary>
    public const string DefaultUpdateUrl = "https://api.github.com/repos/jaydenland13-cyber/museek/releases/latest";

    public static string Version
    {
        get
        {
            var v = typeof(AppInfo).Assembly.GetName().Version;
            return v == null ? "1.0.0" : $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
        }
    }
}
