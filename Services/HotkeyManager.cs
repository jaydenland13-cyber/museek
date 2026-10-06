using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace Museek.Services;

/// <summary>
/// OS-wide hotkeys via RegisterHotKey on a hidden message-only window.
/// Callbacks fire on the UI thread.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _callbacks = new();
    private int _nextId = 1;

    public HotkeyManager()
    {
        var p = new HwndSourceParameters("MuseekHotkeys") { ParentWindow = new IntPtr(-3) }; // HWND_MESSAGE
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    /// <summary>Registers e.g. "ctrl+alt+space". Returns an id, or -1 on failure.</summary>
    public int Register(string combo, Action callback)
    {
        if (!TryParse(combo, out var mods, out var vk))
        {
            Log.Error($"Failed to register hotkey {combo}: could not parse");
            return -1;
        }
        var id = _nextId++;
        if (!RegisterHotKey(_source.Handle, id, mods | MOD_NOREPEAT, vk))
        {
            Log.Error($"Failed to register hotkey {combo}: already in use by another app?");
            return -1;
        }
        _callbacks[id] = callback;
        Log.Info($"Registered hotkey {combo}");
        return id;
    }

    public void Unregister(int id)
    {
        if (id < 0) return;
        UnregisterHotKey(_source.Handle, id);
        _callbacks.Remove(id);
    }

    public void UnregisterAll()
    {
        foreach (var id in new List<int>(_callbacks.Keys)) Unregister(id);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _callbacks.TryGetValue(wParam.ToInt32(), out var cb))
        {
            handled = true;
            try { cb(); } catch (Exception ex) { Log.Error($"Hotkey callback failed: {ex.Message}"); }
        }
        return IntPtr.Zero;
    }

    private static bool TryParse(string combo, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        foreach (var raw in combo.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= MOD_CONTROL; break;
                case "alt": mods |= MOD_ALT; break;
                case "shift": mods |= MOD_SHIFT; break;
                case "win": case "windows": mods |= MOD_WIN; break;
                default:
                    var name = raw switch { "esc" => "Escape", "del" => "Delete", "enter" => "Return", _ => raw };
                    if (name.Length == 1 && char.IsDigit(name[0])) name = "D" + name;
                    if (!Enum.TryParse<Key>(name, true, out var key)) return false;
                    vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                    break;
            }
        }
        return vk != 0;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.Dispose();
    }
}
