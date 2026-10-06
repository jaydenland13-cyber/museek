using System;
using Microsoft.Win32;

namespace Museek.Services;

/// <summary>"Start with Windows" via the per-user Run registry key (no admin needed).</summary>
public static class AutostartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Museek";

    private static string? ExePath => Environment.ProcessPath;

    public static bool IsAvailable => !string.IsNullOrEmpty(ExePath) &&
        !ExePath!.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase);

    public static bool IsEnabled()
    {
        if (!IsAvailable) return false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue(ValueName) is string v &&
                   v.Trim('"').Equals(ExePath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) { Log.Warn($"Could not read autostart registry key: {ex.Message}"); return false; }
    }

    public static bool SetEnabled(bool enabled)
    {
        if (!IsAvailable) return false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) key.SetValue(ValueName, $"\"{ExePath}\"", RegistryValueKind.String);
            else key.DeleteValue(ValueName, false);
            Log.Info($"Start with Windows: {(enabled ? "enabled" : "disabled")}");
            return true;
        }
        catch (Exception ex) { Log.Warn($"Could not update autostart registry key: {ex.Message}"); return false; }
    }
}
