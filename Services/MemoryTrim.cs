using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Museek.Services;

/// <summary>Releases unused memory back to Windows (debounced). Cheap, and keeps Task Manager numbers low.</summary>
public static class MemoryTrim
{
    [DllImport("kernel32.dll")] private static extern bool SetProcessWorkingSetSize(IntPtr proc, IntPtr min, IntPtr max);

    private static DispatcherTimer? _timer;

    public static void RunSoon(int seconds = 8)
    {
        _timer ??= new DispatcherTimer();
        _timer.Stop();
        _timer.Interval = TimeSpan.FromSeconds(seconds);
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private static void OnTick(object? sender, EventArgs e)
    {
        _timer?.Stop();
        try
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, new IntPtr(-1), new IntPtr(-1));
        }
        catch { /* best effort */ }
    }
}
